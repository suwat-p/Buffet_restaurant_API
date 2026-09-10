using Buffet_Restaurant_API.Dtos;
using Buffet_Restaurant_API.Models;
using Buffet_Restaurant_Managment_System_API.Data;
using Buffet_Restaurant_Managment_System_API.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using QRCoder;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Buffet_Restaurant_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrderController : ControllerBase
    {
        private readonly restaurantDbContext _context;
        private readonly IHubContext<tableStatusHub> _hubContext;
        private readonly IConfiguration _configuration;

        public OrderController(restaurantDbContext context, IHubContext<tableStatusHub> hubContext, IConfiguration configuration)
        {
            _context = context;
            _hubContext = hubContext;
            _configuration = configuration;
        }

        private string GetFrontendBaseUrl()
        {
            return _configuration["FrontendBaseUrl"] ?? "https://buffet-restaurant-management-system.vercel.app";
        }

        [HttpPost("checkout")]
        public async Task<IActionResult> PlaceOrder([FromBody] OrderDto dto)
        {
            var cart = await _context.Carts.FindAsync(dto.CartId);
            if (cart == null)
            {
                return NotFound(new { message = "ไม่พบตะกร้าสินค้าที่ระบุ" });
            }

            var cartItems = await _context.CartItems
                .Where(ci => ci.Cart_id == dto.CartId)
                .ToListAsync();

            if (!cartItems.Any())
            {
                return BadRequest(new { message = "ไม่มีรายการสินค้าในตะกร้า" });
            }

            bool isPreOrder = dto.OrderType?.ToLower() == "preorder" || dto.OrderType == "สั่งล่วงหน้า";
            string orderTypeDisplay = isPreOrder ? "สั่งล่วงหน้า" : "สั่งหน้าร้าน";
            // 🟢 สั่งหน้าร้าน -> ครัวรับเข้าเตรียมทันที (กำลังจัดเตรียมอาหาร)
            // 🟢 สั่งล่วงหน้า -> เป็นแค่รับคำสั่งไว้ก่อน รอถึงรอบเวลาจองค่อยเริ่มเตรียม (รับออเดอร์)
            string initialStatus = isPreOrder ? "รับออเดอร์" : "กำลังจัดเตรียมอาหาร";

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                int finalBillId = 0;

                // 🟢 1. จัดการหา Bill_id จาก BookingId หรือ BillId ที่ส่งมา
                if (dto.BillId.HasValue && dto.BillId.Value > 0)
                {
                    finalBillId = dto.BillId.Value;
                }
                else if (dto.BookingId.HasValue && dto.BookingId.Value > 0)
                {
                    // ค้นหาบิลที่มีอยู่แล้วจากการจองนี้
                    var existingBill = await _context.Bill
                        .FirstOrDefaultAsync(b => b.Booking_id == dto.BookingId.Value);

                    if (existingBill != null)
                    {
                        finalBillId = existingBill.Bill_id;
                    }
                    else
                    {
                        // ถ้ายังไม่มี ให้เปิดบิลใหม่พร้อมใส่ค่า Default
                        var newBill = new Bill
                        {
                            Booking_id = dto.BookingId.Value,
                            Config_id = 30001, // Default Config/Package
                            Emp_id = 1,        // Default พนักงาน/ระบบ
                            Created_at = DateTime.Now
                        };

                        _context.Bill.Add(newBill);
                        await _context.SaveChangesAsync();
                        finalBillId = newBill.Bill_id;
                    }
                }

                if (finalBillId == 0)
                {
                    return BadRequest(new { message = "ไม่สามารถระบุบิลสำหรับออเดอร์นี้ได้ กรุณาตรวจสอบ BookingId หรือ BillId" });
                }

                // 🟢 2. สร้าง Order
                var newOrder = new Orders
                {
                    Bill_id = finalBillId,
                    Order_type = orderTypeDisplay,
                    OrderDateTime = DateTime.Now,
                    Order_Status = initialStatus
                };

                _context.Orders.Add(newOrder);
                await _context.SaveChangesAsync();

                var menuIds = cartItems.Select(ci => ci.Menu_id).ToList();
                var menus = await _context.Menus
                    .Where(m => menuIds.Contains(m.Menu_id))
                    .ToDictionaryAsync(m => m.Menu_id, m => m);

                var orderDetails = new List<Order_detail>();

                foreach (var item in cartItems)
                {
                    decimal currentPrice = menus.TryGetValue(item.Menu_id, out var menuObj) ? (menuObj.Price ?? 0m) : 0m;

                    orderDetails.Add(new Order_detail
                    {
                        Order_id = newOrder.Order_id,
                        Menu_id = item.Menu_id, // 🟢 แก้เป็น Menu_id (M ตัวใหญ่)
                        Quantity = item.Quantity,
                        PriceAtOrderTime = currentPrice
                    });
                }

                _context.Order_detail.AddRange(orderDetails);
                await _context.SaveChangesAsync();

                _context.CartItems.RemoveRange(cartItems);
                await _context.SaveChangesAsync();

                _context.Carts.Remove(cart);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                // 🟢 3. แจ้งเตือนห้องครัวผ่าน SignalR เฉพาะสั่งหน้าร้าน
                if (!isPreOrder)
                {
                    await _hubContext.Clients.All.SendAsync("NewKitchenOrder", newOrder.Order_id);
                }

                return Ok(new
                {
                    message = $"สั่งอาหาร ({orderTypeDisplay}) เรียบร้อยแล้ว",
                    orderId = newOrder.Order_id,   // 🟢 เปลี่ยนเป็น camelCase ไม่มี underscore ให้ตรงกับ property อื่นๆ ของ API (OrderId, OrderStatus)
                    billId = finalBillId,
                    orderType = orderTypeDisplay,
                    orderStatus = initialStatus
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                var detailedError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return StatusCode(500, new { message = "เกิดข้อผิดพลาดในการสั่งอาหาร", error = detailedError });
            }
        }

        // 🍳 2. ดึงสลิปใบบิลครัว + เจน QR Code
        [HttpGet("getKitchenTicket/{orderId}")]
        public async Task<IActionResult> GetKitchenTicket(int orderId, [FromServices] Cloudinary cloudinary = null)
        {
            var order = await _context.Orders
                .Where(o => o.Order_id == orderId)
                .Select(o => new
                {
                    OrderId = o.Order_id,
                    OrderTime = o.OrderDateTime,
                    OrderStatus = o.Order_Status,
                    BillId = o.Bill_id
                })
                .FirstOrDefaultAsync();

            if (order == null) return NotFound(new { message = "ไม่พบรายการออเดอร์" });

            var bill = await _context.Bill.FirstOrDefaultAsync(b => b.Bill_id == order.BillId);

            // 2. ค้นหาเลขโต๊ะจาก Bill_id หรือ Booking_id
            var tableNumbers = await (from gt in _context.GroupTables
                                      join t in _context.Tables on gt.Table_id equals t.Table_id
                                      where (gt.Bill_id == order.BillId) ||
                                            (bill != null && bill.Booking_id != null && gt.Booking_id == bill.Booking_id)
                                      select t.Table_Number)
                                      .Distinct()
                                      .ToListAsync();

            string tableDisplay = tableNumbers.Any() ? string.Join(", ", tableNumbers) : "ไม่ระบุโต๊ะ";

            var items = await (from od in _context.Order_detail
                               join m in _context.Menus on od.Menu_id equals m.Menu_id // 🟢 แก้เป็น od.Menu_id
                               where od.Order_id == orderId
                               select new
                               {
                                   MenuId = od.Menu_id, // 🟢 แก้เป็น od.Menu_id
                                   MenuName = m.Menu_Name,
                                   Quantity = od.Quantity
                               }).ToListAsync();

            string serveUrl = $"{GetFrontendBaseUrl()}/serve-action?orderId={orderId}";
            string qrCodeCloudinaryUrl = "";

            if (cloudinary != null)
            {
                try
                {
                    using (QRCodeGenerator qrGenerator = new QRCodeGenerator())
                    {
                        QRCodeData qrCodeData = qrGenerator.CreateQrCode(serveUrl, QRCodeGenerator.ECCLevel.Q);
                        PngByteQRCode qrCode = new PngByteQRCode(qrCodeData);
                        byte[] qrCodeBytes = qrCode.GetGraphic(20);

                        using (var stream = new MemoryStream(qrCodeBytes))
                        {
                            var uploadParams = new ImageUploadParams()
                            {
                                File = new FileDescription($"qr_serve_order_{orderId}.png", stream),
                                Folder = "restaurant_serve_qrcodes",
                                PublicId = $"order_serve_{orderId}_{Guid.NewGuid()}"
                            };

                            var uploadResult = await cloudinary.UploadAsync(uploadParams);
                            if (uploadResult.Error == null)
                            {
                                qrCodeCloudinaryUrl = uploadResult.SecureUrl.ToString();
                            }
                        }
                    }
                }
                catch { }
            }

            var printItems = items.Select(i => (i.MenuName, i.Quantity)).ToList();

            // 🟢 แก้ไขเพื่อยกเลิกการเตือน Async
            Task.Run(() => EscPosPrinterHelper.PrintKitchenTicket(order.OrderId, tableDisplay, order.OrderTime, printItems, GetFrontendBaseUrl()));

            return Ok(new
            {
                OrderId = order.OrderId,
                TableNumber = tableDisplay,
                OrderTime = order.OrderTime,
                OrderStatus = order.OrderStatus,
                ServeQrCode = qrCodeCloudinaryUrl,
                Items = items
            });
        }

        [HttpGet("{orderId}/serve")]
        [HttpPost("{orderId}/serve")]
        public async Task<IActionResult> ServeOrder(int orderId)
        {
            try
            {
                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Order_id == orderId);
                if (order == null) return NotFound(new { message = "ไม่พบรายการออเดอร์" });

                order.Order_Status = "เสร็จสิ้น";
                _context.Entry(order).State = EntityState.Modified;
                await _context.SaveChangesAsync();

                try
                {
                    // 🟢 แก้ไข: ส่งข้อมูลครอบคลุมทั้ง orderId, status และ billId
                    await _hubContext.Clients.All.SendAsync("OrderStatusUpdated", new
                    {
                        orderId = orderId,
                        status = "เสร็จสิ้น",
                        billId = order.Bill_id
                    });
                }
                catch (Exception hubEx)
                {
                    Console.WriteLine($"แจ้งเตือน SignalR ไม่สำเร็จ: {hubEx.Message}");
                }

                return Ok(new { message = "นำเสิร์ฟเรียบร้อยแล้ว", orderId = orderId, status = order.Order_Status });
            }
            catch (Exception ex)
            {
                var detailedError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return StatusCode(500, new { message = "เกิดข้อผิดพลาดในการอัปเดตสถานะเสิร์ฟ", error = detailedError });
            }
        }

        // 🔍 4. ดึงรายละเอียดออเดอร์
        [HttpGet("getOrderDetails/{orderId}")]
        public async Task<IActionResult> GetOrderDetails(int orderId)
        {
            var orderDetails = await _context.Order_detail
                .Where(od => od.Order_id == orderId)
                .Select(od => new
                {
                    Menu_id = od.Menu_id, // 🟢 แก้เป็น od.Menu_id
                    Quantity = od.Quantity,
                    PriceAtOrderTime = od.PriceAtOrderTime
                })
                .ToListAsync();

            if (orderDetails == null || !orderDetails.Any())
            {
                return NotFound(new { message = "ไม่พบข้อมูลคำสั่งซื้อ" });
            }

            return Ok(orderDetails);
        }

        // 📡 4.5 ดึงสถานะออเดอร์ + รายการสินค้า สำหรับหน้า Track Order ของลูกค้า
        // (เบากว่า GetKitchenTicket เพราะไม่พิมพ์ใบเสร็จ/สร้าง QR ทุกครั้งที่เรียก
        //  ใช้ตอนโหลดหน้าครั้งแรก จากนั้นให้ SignalR "OrderStatusUpdated" อัปเดตแบบ real-time ต่อ)
        [HttpGet("getOrderStatus/{orderId}")]
        public async Task<IActionResult> GetOrderStatus(int orderId)
        {
            var order = await _context.Orders
                .Where(o => o.Order_id == orderId)
                .Select(o => new
                {
                    OrderId = o.Order_id,
                    OrderStatus = o.Order_Status,
                    BillId = o.Bill_id
                })
                .FirstOrDefaultAsync();

            if (order == null) return NotFound(new { message = "ไม่พบรายการออเดอร์" });

            var items = await (from od in _context.Order_detail
                               join m in _context.Menus on od.Menu_id equals m.Menu_id
                               where od.Order_id == orderId
                               select new
                               {
                                   MenuId = od.Menu_id,
                                   MenuName = m.Menu_Name,
                                   Quantity = od.Quantity
                               }).ToListAsync();

            return Ok(new
            {
                OrderId = order.OrderId,
                OrderStatus = order.OrderStatus,
                Items = items
            });
        }

        // 📲 5. ดึงข้อมูลเสิร์ฟ + บันทึกลง DB เปลี่ยนสถานะเป็น "กำลังนำเสิร์ฟ"
        [HttpGet("getServeInfo/{orderId}")]
        [HttpPost("getServeInfo/{orderId}")]
        public async Task<IActionResult> GetServeInfo(int orderId)
        {
            try
            {
                var order = await _context.Orders.FirstOrDefaultAsync(o => o.Order_id == orderId);
                if (order == null) return NotFound(new { message = "ไม่พบรายการออเดอร์" });

                if (order.Order_Status != "เสร็จสิ้น" && order.Order_Status != "กำลังนำเสิร์ฟ")
                {
                    order.Order_Status = "กำลังนำเสิร์ฟ";
                    _context.Entry(order).State = EntityState.Modified;
                    await _context.SaveChangesAsync();

                    try
                    {
                        // 🟢 แก้ไข: ส่งข้อมูลครอบคลุมทั้ง orderId, status และ billId
                        await _hubContext.Clients.All.SendAsync("OrderStatusUpdated", new
                        {
                            orderId = orderId,
                            status = "กำลังนำเสิร์ฟ",
                            billId = order.Bill_id
                        });
                    }
                    catch (Exception hubEx)
                    {
                        Console.WriteLine($"SignalR Error: {hubEx.Message}");
                    }
                }

                var tableNumbers = await (from gt in _context.GroupTables
                                          join t in _context.Tables on gt.Table_id equals t.Table_id
                                          where gt.Bill_id == order.Bill_id
                                          select t.Table_Number).ToListAsync();

                string tableDisplay = tableNumbers.Any() ? string.Join(", ", tableNumbers) : "ไม่ระบุโต๊ะ";

                var items = await (from od in _context.Order_detail
                                   join m in _context.Menus on od.Menu_id equals m.Menu_id
                                   where od.Order_id == orderId
                                   select new
                                   {
                                       MenuId = od.Menu_id,
                                       MenuName = m.Menu_Name,
                                       Quantity = od.Quantity
                                   }).ToListAsync();

                return Ok(new
                {
                    OrderId = order.Order_id,
                    TableNumber = tableDisplay,
                    OrderStatus = order.Order_Status,
                    Items = items
                });
            }
            catch (Exception ex)
            {
                var detailedError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return StatusCode(500, new { message = "เกิดข้อผิดพลาดในการดึงข้อมูล/อัปเดตสถานะเสิร์ฟ", error = detailedError });
            }
        }

        // 💳 6. ดึงรายการชำระเงินตาม BillId
        [HttpGet("getBillPricedItems/{billId}")]
        public async Task<IActionResult> GetBillPricedItems(int billId)
        {
            var pricedItems = await (from od in _context.Order_detail
                                     join o in _context.Orders on od.Order_id equals o.Order_id
                                     join m in _context.Menus on od.Menu_id equals m.Menu_id // 🟢 แก้เป็น od.Menu_id
                                     where o.Bill_id == billId
                                           && o.Order_Status == "เสร็จสิ้น"
                                           && od.PriceAtOrderTime > 0
                                     select new
                                     {
                                         od.Orderdetail_id,
                                         od.Order_id,
                                         Menu_id = od.Menu_id, // 🟢 แก้เป็น od.Menu_id
                                         MenuName = m.Menu_Name,
                                         od.Quantity,
                                         od.PriceAtOrderTime,
                                         SubTotal = od.Quantity * od.PriceAtOrderTime
                                     }).ToListAsync();

            if (!pricedItems.Any())
            {
                return Ok(new
                {
                    billId = billId,
                    message = "ไม่มีรายการอาหารที่ต้องชำระเงินเพิ่มในบิลนี้",
                    totalPrice = 0,
                    items = new List<object>()
                });
            }

            return Ok(new
            {
                billId = billId,
                totalPrice = pricedItems.Sum(i => i.SubTotal),
                items = pricedItems
            });
        }
        [HttpGet("getActiveOrdersByBill/{id}")]
        public async Task<IActionResult> GetActiveOrdersByBill(int id)
        {
            int targetBillId = id;

            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Order_id == id);
            if (order != null)
            {
                targetBillId = order.Bill_id ?? id;
            }
            else
            {
                var bill = await _context.Bill.FirstOrDefaultAsync(b => b.Booking_id == id || b.Bill_id == id);
                if (bill != null)
                {
                    targetBillId = bill.Bill_id;
                }
            }

            // 🟢 แก้ไข: ลบเงื่อนไขกั้น status ออก เพื่อดึงประวัติรายการที่เสร็จแล้วกลับมาแสดงตอน Refresh ด้วย
            var activeOrders = await _context.Orders
                .Where(o => o.Bill_id == targetBillId)
                .OrderByDescending(o => o.OrderDateTime)
                .Select(o => new
                {
                    orderId = o.Order_id,
                    orderStatus = o.Order_Status ?? "กำลังจัดเตรียมอาหาร",
                    orderTime = o.OrderDateTime,
                    items = _context.Order_detail
                        .Where(od => od.Order_id == o.Order_id)
                        .Select(od => new
                        {
                            menuId = od.Menu_id,
                            menuName = od.Menu != null ? od.Menu.Menu_Name : "รายการอาหาร",
                            quantity = od.Quantity
                        }).ToList()
                })
                .ToListAsync();

            return Ok(activeOrders);
        }
    }

    public static class EscPosPrinterHelper
    {
        private static readonly string _printerIp = "127.0.0.1";
        private static readonly int _printerPort = 9100;

        public static async Task PrintKitchenTicket(int orderId, string tableNumber, DateTime orderTime, List<(string Name, int Qty)> items, string frontendBaseUrl = "https://buffet-restaurant-management-system.vercel.app")
        {
            using SKBitmap bitmap = DrawTicketImage(orderId, tableNumber, orderTime, items, frontendBaseUrl);
            byte[] imageBytes = ConvertBitmapToEscPosRaster(bitmap);

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_printerIp, _printerPort);
                using var stream = client.GetStream();

                var bytes = new List<byte>();
                bytes.AddRange(new byte[] { 0x1B, 0x40 });               // Reset
                bytes.AddRange(imageBytes);                              // ภาพใบตั๋วครัว
                bytes.Add(0x0A);                                         // Line Feed
                bytes.AddRange(new byte[] { 0x1B, 0x64, 0x03 });         // Feed 3 บรรทัด
                bytes.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 });   // Cut

                byte[] data = bytes.ToArray();
                await stream.WriteAsync(data, 0, data.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"พิมพ์ตั๋วครัวไม่สำเร็จ: {ex.Message}");
            }
        }

        private static SKBitmap DrawTicketImage(int orderId, string tableNumber, DateTime orderTime, List<(string Name, int Qty)> items, string frontendBaseUrl)
        {
            int width = 576;
            int estimatedHeight = 1000;

            SKBitmap bitmap = new SKBitmap(width, estimatedHeight);
            using SKCanvas canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            SKTypeface typeface = SKTypeface.FromFamilyName("Tahoma", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                 ?? SKTypeface.Default;
            SKTypeface boldTypeface = SKTypeface.FromFamilyName("Tahoma", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                 ?? SKTypeface.Default;

            using SKFont fontNormal = new SKFont(typeface, 18);
            using SKFont fontBold = new SKFont(boldTypeface, 20);
            using SKFont fontHeader = new SKFont(boldTypeface, 26);

            using SKPaint paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

            float y = 35;
            float leftMargin = 15;
            float rightMargin = width - 15;

            void DrawTextLeft(string text, float targetY, SKFont font) => canvas.DrawText(text, leftMargin, targetY, SKTextAlign.Left, font, paint);
            void DrawTextRight(string text, float targetY, SKFont font) => canvas.DrawText(text, rightMargin, targetY, SKTextAlign.Right, font, paint);
            void DrawTextCenter(string text, float targetY, SKFont font) => canvas.DrawText(text, width / 2f, targetY, SKTextAlign.Center, font, paint);

            void DrawRow(string left, string right, SKFont? font = null, float lineSpacing = 30)
            {
                var f = font ?? fontNormal;
                DrawTextLeft(left, y, f);
                DrawTextRight(right, y, f);
                y += lineSpacing;
            }

            void DrawDivider()
            {
                using var linePaint = new SKPaint
                {
                    Color = SKColors.Gray,
                    StrokeWidth = 1.5f,
                    PathEffect = SKPathEffect.CreateDash(new float[] { 6, 3 }, 0)
                };
                canvas.DrawLine(leftMargin, y - 8, rightMargin, y - 8, linePaint);
                y += 12;
            }

            DrawTextCenter("ร้าน BUFFET", y, fontHeader);
            y += 35;
            DrawDivider();
            y += 5;

            DrawRow("เลขที่ใบเสร็จ:", $"B{orderId:D5}");
            DrawRow("วันที่:", orderTime.ToString("dd/MM/yyyy HH:mm:ss"));
            DrawRow("โต๊ะ:", tableNumber);

            DrawDivider();
            y += 5;

            foreach (var item in items)
            {
                DrawRow($"{item.Name}:", $"{item.Qty}");
            }

            DrawDivider();
            y += 5;

            string serveUrl = $"{frontendBaseUrl}/serve-action?orderId={orderId}";
            using (var qrGen = new QRCodeGenerator())
            {
                var qrData = qrGen.CreateQrCode(serveUrl, QRCodeGenerator.ECCLevel.Q);
                var qrPng = new PngByteQRCode(qrData);
                byte[] qrBytes = qrPng.GetGraphic(5);

                using SKBitmap qrBitmap = SKBitmap.Decode(qrBytes);
                int qrSize = 180;
                float qrX = (width - qrSize) / 2f;
                var srcRect = new SKRect(0, 0, qrBitmap.Width, qrBitmap.Height);
                var destRect = new SKRect(qrX, y, qrX + qrSize, y + qrSize);
                canvas.DrawBitmap(qrBitmap, srcRect, destRect, SKSamplingOptions.Default, paint);
                y += qrSize + 15;
            }

            int finalHeight = (int)y;
            SKBitmap cropped = new SKBitmap(width, finalHeight);
            using (SKCanvas cropCanvas = new SKCanvas(cropped))
            {
                var rect = new SKRect(0, 0, width, finalHeight);
                cropCanvas.DrawBitmap(bitmap, rect, rect, SKSamplingOptions.Default, paint);
            }
            return cropped;
        }

        private static byte[] ConvertBitmapToEscPosRaster(SKBitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int widthBytes = (width + 7) / 8;

            List<byte> bytes = new List<byte>();
            bytes.AddRange(new byte[] { 0x1D, 0x76, 0x30, 0x00 });
            bytes.Add((byte)(widthBytes % 256));
            bytes.Add((byte)(widthBytes / 256));
            bytes.Add((byte)(height % 256));
            bytes.Add((byte)(height / 256));

            for (int y = 0; y < height; y++)
            {
                for (int xByte = 0; xByte < widthBytes; xByte++)
                {
                    byte b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int x = (xByte * 8) + bit;
                        if (x < width)
                        {
                            SKColor color = bitmap.GetPixel(x, y);
                            int luminance = (int)(color.Red * 0.3 + color.Green * 0.59 + color.Blue * 0.11);
                            if (luminance < 128)
                            {
                                b |= (byte)(0x80 >> bit);
                            }
                        }
                    }
                    bytes.Add(b);
                }
            }

            return bytes.ToArray();
        }


    }
}