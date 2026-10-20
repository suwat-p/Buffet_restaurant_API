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
using System.Globalization;
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

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                int finalBillId = 0;

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

                        var newBill = new Bill
                        {
                            Booking_id = dto.BookingId.Value,
                            Config_id = 30001,
                            Emp_id = 1,
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

                // เช็คว่าการจองของบิลนี้เช็คอินแล้วหรือยัง (เช็คอินแล้ว = Completed)
                bool alreadyCheckedIn = await (
                    from b in _context.Bill
                    from bk in _context.Bookings
                    where b.Bill_id == finalBillId
                          && b.Booking_id == bk.Booking_id
                          && bk.Booking_Status == "Completed"
                    select b.Bill_id).AnyAsync();

                // สั่งหน้าร้าน หรือ สั่งล่วงหน้าแต่เช็คอินแล้ว -> เข้าครัวทันที
                bool sendToKitchen = !isPreOrder || alreadyCheckedIn;
                string initialStatus = sendToKitchen ? "กำลังจัดเตรียมอาหาร" : "รับออเดอร์";

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
                        Menu_id = item.Menu_id,
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

                if (sendToKitchen)
                {
                    await _hubContext.Clients.All.SendAsync("NewKitchenOrder", newOrder.Order_id);

                    // ให้หน้าติดตามสถานะของลูกค้า (StatusPreOrder) อัปเดตทันที
                    await _hubContext.Clients.All.SendAsync("OrderStatusUpdated", new
                    {
                        orderId = newOrder.Order_id,
                        status = initialStatus,
                        billId = finalBillId
                    });

                    //  พิมพ์บิลครัวจากฝั่ง API ทันที ไม่ต้องรอให้มีหน้าแดชบอร์ดครัวเปิดอยู่

                    var (printed, printMessage) = await PrintKitchenTicketForOrderAsync(_context, newOrder.Order_id, GetFrontendBaseUrl());
                    if (!printed) Console.WriteLine($"[Order {newOrder.Order_id}] {printMessage}");
                }

                return Ok(new
                {
                    message = $"สั่งอาหาร ({orderTypeDisplay}) เรียบร้อยแล้ว",
                    orderId = newOrder.Order_id,
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

        //ดึงสลิปใบบิลครัว + เจน QR Code + สั่งพิมพ์ 
        [HttpGet("getKitchenTicket/{orderId}")]
        public async Task<IActionResult> GetKitchenTicket(int orderId, [FromQuery] bool print = false, [FromServices] Cloudinary cloudinary = null)
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

            // ค้นหาเลขโต๊ะจาก Bill_id หรือ Booking_id
            var tableNumbers = await (from gt in _context.GroupTables
                                      join t in _context.Tables on gt.Table_id equals t.Table_id
                                      where (gt.Bill_id == order.BillId) ||
                                            (bill != null && bill.Booking_id != null && gt.Booking_id == bill.Booking_id)
                                      select t.Table_Number)
                                      .Distinct()
                                      .ToListAsync();

            string tableDisplay = tableNumbers.Any() ? string.Join(", ", tableNumbers) : "ไม่ระบุโต๊ะ";

            var items = await (from od in _context.Order_detail
                               join m in _context.Menus on od.Menu_id equals m.Menu_id
                               where od.Order_id == orderId
                               select new
                               {
                                   MenuId = od.Menu_id,
                                   MenuName = m.Menu_Name,
                                   Quantity = od.Quantity,
                                   Price = od.PriceAtOrderTime   // ใช้แยกรายการบุฟเฟต์ (0) กับรายการที่คิดเงิน (เช่น เบียร์)
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


            // พิมพ์ซ้ำได้โดยเรียก getKitchenTicket/{orderId}?print=true (ค่าปกติไม่พิมพ์ เพราะ PlaceOrder พิมพ์ให้แล้ว)
            bool printed = false;
            string printMessage = "ไม่ได้สั่งพิมพ์ (ใส่ ?print=true เพื่อพิมพ์ซ้ำ)";
            if (print)
            {
                (printed, printMessage) = await PrintKitchenTicketForOrderAsync(_context, orderId, GetFrontendBaseUrl());
            }

            return Ok(new
            {
                OrderId = order.OrderId,
                TableNumber = tableDisplay,
                OrderTime = order.OrderTime,
                OrderStatus = order.OrderStatus,
                ServeQrCode = qrCodeCloudinaryUrl,
                Items = items.Select(i => new { i.MenuId, i.MenuName, i.Quantity }),
                Printed = printed,
                PrintMessage = printMessage
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
                    // ส่งข้อมูลครอบคลุมทั้ง orderId, status และ billId
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

        // ดึงรายละเอียดออเดอร์
        [HttpGet("getOrderDetails/{orderId}")]
        public async Task<IActionResult> GetOrderDetails(int orderId)
        {
            var orderDetails = await _context.Order_detail
                .Where(od => od.Order_id == orderId)
                .Select(od => new
                {
                    Menu_id = od.Menu_id,
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

        //  ดึงสถานะออเดอร์ + รายการสินค้า สำหรับหน้า Track Order ของลูกค้า
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

        //  ดึงข้อมูลเสิร์ฟ + บันทึกลง DB เปลี่ยนสถานะเป็น "กำลังนำเสิร์ฟ"
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
                        //  ส่งข้อมูลครอบคลุมทั้ง orderId, status และ billId
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

        //  ดึงรายการชำระเงินตาม BillId
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
                                         Menu_id = od.Menu_id,
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

            // ลบเงื่อนไขกั้น status ออก เพื่อดึงประวัติรายการที่เสร็จแล้วกลับมาแสดงตอน Refresh ด้วย
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

        // โหลดข้อมูลออเดอร์แล้วสั่งพิมพ์บิลครัว — เรียกจาก OrderController (สั่งหน้าร้าน) และ BookingController (เช็คอินออเดอร์ล่วงหน้า)
        public static async Task<(bool Printed, string Message)> PrintKitchenTicketForOrderAsync(restaurantDbContext context, int orderId, string frontendBaseUrl)
        {
            try
            {
                var order = await context.Orders
                    .Where(o => o.Order_id == orderId)
                    .Select(o => new { o.Order_id, o.OrderDateTime, o.Bill_id })
                    .FirstOrDefaultAsync();

                if (order == null) return (false, "ไม่พบรายการออเดอร์");

                var bill = await context.Bill.FirstOrDefaultAsync(b => b.Bill_id == order.Bill_id);

                var tableNumbers = await (from gt in context.GroupTables
                                          join t in context.Tables on gt.Table_id equals t.Table_id
                                          where (gt.Bill_id == order.Bill_id) ||
                                                (bill != null && bill.Booking_id != null && gt.Booking_id == bill.Booking_id)
                                          select t.Table_Number)
                                          .Distinct()
                                          .ToListAsync();

                string tableDisplay = tableNumbers.Any() ? string.Join(", ", tableNumbers) : "ไม่ระบุโต๊ะ";

                var items = await (from od in context.Order_detail
                                   join m in context.Menus on od.Menu_id equals m.Menu_id
                                   where od.Order_id == orderId
                                   select new { m.Menu_Name, od.Quantity, od.PriceAtOrderTime }).ToListAsync();

                var printItems = items.Select(i => (i.Menu_Name, i.Quantity, i.PriceAtOrderTime)).ToList();

                await PrintKitchenTicketAsync(order.Order_id, tableDisplay, order.OrderDateTime, printItems, frontendBaseUrl);
                return (true, "สั่งพิมพ์บิลครัวเรียบร้อยแล้ว (Image Mode)");
            }
            catch (Exception ex)
            {
                string msg = $"พิมพ์บิลครัวไม่สำเร็จ: {ex.Message}";
                Console.WriteLine(msg);
                return (false, msg);
            }
        }

        private static async Task PrintKitchenTicketAsync(
            int orderId,
            string tableNumber,
            DateTime orderTime,
            List<(string Name, int Qty, decimal Price)> items,
            string frontendBaseUrl)
        {
            using (SKBitmap ticketBitmap = DrawKitchenTicketImage(orderId, tableNumber, orderTime, items, frontendBaseUrl))
            {
                byte[] imageBytes = ConvertBitmapToEscPosRaster(ticketBitmap);

                string ipAddress = "127.0.0.1";
                int port = 9100;

                using (TcpClient client = new TcpClient(ipAddress, port))
                using (NetworkStream stream = client.GetStream())
                {
                    List<byte> bytes = new List<byte>();

                    bytes.AddRange(new byte[] { 0x1B, 0x40 });             // Reset
                    bytes.AddRange(imageBytes);                            // Image
                    bytes.AddRange(new byte[] { 0x1B, 0x64, 0x03 });       // Feed
                    bytes.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 }); // Cut

                    byte[] data = bytes.ToArray();
                    await stream.WriteAsync(data, 0, data.Length);
                }
            }
        }

        // สร้าง QR
        private static SKBitmap CreateQrBitmap(string payload, int pixelsPerModule, int maxWidth)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

            int modules = data.ModuleMatrix.Count;   // รวม quiet zone แล้ว
            int ppm = pixelsPerModule;
            while (ppm > 3 && modules * ppm > maxWidth) ppm--;

            byte[] png = new PngByteQRCode(data).GetGraphic(ppm);
            return SKBitmap.Decode(png);
        }

        // วาดบิลครัวตามรูปใบเสร็จ

        private static SKBitmap DrawKitchenTicketImage(
            int orderId,
            string tableNumber,
            DateTime orderTime,
            List<(string Name, int Qty, decimal Price)> items,
            string frontendBaseUrl)
        {
            int width = 576;                 // 80mm @ 203dpi
            float lineSpacing = 50;          // ปรับระยะห่างบรรทัดตรงนี้
            int qrModulePx = 8;              // ขนาดจุด QR (px ต่อโมดูล) ยิ่งมากยิ่งใหญ่/สแกนง่าย: 6=เล็ก 8=แนะนำ 10=ใหญ่

            // สร้าง QR ก่อน เพื่อให้รู้ขนาดจริงไว้คำนวณความสูงบิล
            string serveUrl = $"{frontendBaseUrl}/serve-action?orderId={orderId}";
            using SKBitmap qrBitmap = CreateQrBitmap(serveUrl, qrModulePx, width - 40);

            int estimatedHeight = 700 + (int)(items.Count * lineSpacing) + qrBitmap.Height;   // คำนวณตามจำนวนรายการ

            SKBitmap bitmap = new SKBitmap(width, estimatedHeight);
            using (SKCanvas canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);

                SKTypeface typeface = SKTypeface.FromFamilyName("Tahoma", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                     ?? SKTypeface.Default;

                using SKFont fontNormal = new SKFont(typeface, 26);   // ปรับขนาดตัวอักษรตรงนี้
                using SKFont fontHeader = new SKFont(typeface, 38);

                using SKPaint paint = new SKPaint
                {
                    Color = SKColors.Black,
                    IsAntialias = true
                };

                float y = 62;
                float leftMargin = 15;
                float rightMargin = width - 15;

                void DrawTextLeft(string text, float x, float targetY, SKFont font)
                {
                    canvas.DrawText(text, x, targetY, SKTextAlign.Left, font, paint);
                }

                void DrawTextRight(string text, float x, float targetY, SKFont font)
                {
                    canvas.DrawText(text, x, targetY, SKTextAlign.Right, font, paint);
                }

                void DrawTextCenter(string text, float targetY, SKFont font)
                {
                    canvas.DrawText(text, width / 2f, targetY, SKTextAlign.Center, font, paint);
                }

                void DrawRow(string left, string right, SKFont? font = null, float spacing = 50)
                {
                    SKFont currentFont = font ?? fontNormal;
                    DrawTextLeft(left, leftMargin, y, currentFont);
                    DrawTextRight(right, rightMargin, y, currentFont);
                    y += spacing;
                }

                void DrawDivider()
                {
                    // สีดำ: สีเทา (128) ตอนแปลงเป็นขาว-ดำอาจหายไปเมื่อพิมพ์จริง
                    using SKPaint linePaint = new SKPaint
                    {
                        Color = SKColors.Black,
                        StrokeWidth = 2,
                        PathEffect = SKPathEffect.CreateDash(new float[] { 8, 4 }, 0)
                    };
                    canvas.DrawLine(leftMargin, y - 24, rightMargin, y - 24, linePaint);
                    y += 26;
                }

                // วันที่ แบบ พ.ศ. ตามรูป (28/09/2568) — ไม่พึ่ง culture th-TH บนเซิร์ฟเวอร์
                var ci = CultureInfo.InvariantCulture;
                string dateText = $"{orderTime.ToString("dd/MM", ci)}/{orderTime.Year + 543} {orderTime.ToString("HH:mm:ss", ci)}";

                // --- HEADER ---
                DrawTextCenter("ร้าน BUFFET", y, fontHeader);
                y += 57;
                DrawDivider();

                // --- INFO ---
                DrawRow("เลขที่ใบเสร็จ:", $"B{orderId:D5}");
                DrawRow("วันที่:", dateText);
                DrawRow("โต๊ะ:", tableNumber);
                DrawDivider();

                // --- ITEMS ---
                var freeItems = items.Where(i => i.Price <= 0).ToList();   // รายการบุฟเฟต์
                var paidItems = items.Where(i => i.Price > 0).ToList();    // รายการที่คิดเงินเพิ่ม (เบียร์ ฯลฯ)

                foreach (var item in freeItems)
                {
                    DrawRow($"{item.Name}:", $"{item.Qty}");
                }

                if (freeItems.Count > 0 && paidItems.Count > 0)
                {
                    DrawDivider();
                }

                foreach (var item in paidItems)
                {
                    DrawRow($"{item.Name}:", $"{item.Qty}");
                }

                DrawDivider();
                y += 8;

                // --- QR --- วาด 1:1 ไม่ย่อ/ขยาย ขอบคม โมดูลเท่ากันทุกจุด
                float qrX = (float)Math.Round((width - qrBitmap.Width) / 2f);
                canvas.DrawBitmap(qrBitmap, qrX, y);
                y += qrBitmap.Height + 40;

                int finalHeight = Math.Min((int)Math.Ceiling(y), estimatedHeight);
                SKBitmap croppedBitmap = new SKBitmap(width, finalHeight);
                using (SKCanvas cropCanvas = new SKCanvas(croppedBitmap))
                {
                    cropCanvas.Clear(SKColors.White);   // กันส่วนโปร่งใสถูกพิมพ์เป็นสีดำ
                    SKRect srcRect = new SKRect(0, 0, width, finalHeight);
                    SKRect destRect = new SKRect(0, 0, width, finalHeight);
                    cropCanvas.DrawBitmap(bitmap, srcRect, destRect, SKSamplingOptions.Default, paint);
                }

                bitmap.Dispose();
                return croppedBitmap;
            }
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