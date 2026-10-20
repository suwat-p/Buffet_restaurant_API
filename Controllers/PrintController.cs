using Buffet_Restaurant_API.Models;
using Buffet_Restaurant_Managment_System_API.Data;
using Buffet_Restaurant_Managment_System_API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Buffet_Restaurant_Managment_System_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PrintController : ControllerBase
    {
        private readonly restaurantDbContext _context;

        public PrintController(restaurantDbContext context)
        {
            _context = context;
        }

        [HttpGet("print/{billId}")]
        public async Task<IActionResult> PrintBill(int billId, string employeeName = "ไม่ระบุ")
        {
            var bill = await _context.Bill.FirstOrDefaultAsync(b => b.Bill_id == billId);
            if (bill == null) return NotFound(new { success = false, message = "ไม่พบข้อมูลบิล" });

            var config = await _context.Res_Config.FirstOrDefaultAsync(c => c.Config_id == bill.Config_id)
                        ?? await _context.Res_Config.FirstOrDefaultAsync();

            string restaurantName = config?.Res_name ?? "ยินหนึ่งหมูกระทะ";
            decimal adultUnitPrice = config?.Price_Adult ?? 0;
            decimal childUnitPrice = config?.Price_Child ?? 0;
            decimal finePerKg = config?.Fine ?? 0;

            string tableNumbers = "-";
            var tables = await (from gt in _context.GroupTables
                                join t in _context.Tables on gt.Table_id equals t.Table_id
                                where gt.Bill_id == billId
                                select t.Table_Number).ToListAsync();
            if (tables.Any()) tableNumbers = string.Join(", ", tables);

            string staffName = employeeName;

            var orderItems = await (from od in _context.Order_detail
                                    join o in _context.Orders on od.Order_id equals o.Order_id
                                    join m in _context.Menus on od.Menu_id equals m.Menu_id
                                    where o.Bill_id == billId && od.PriceAtOrderTime > 0
                                    select new OrderItemDto
                                    {
                                        MenuName = m.Menu_Name,
                                        Quantity = od.Quantity,
                                        Price = od.PriceAtOrderTime
                                    }).ToListAsync();

            using (SKBitmap receiptBitmap = DrawReceiptImage(
                restaurantName,
                bill,
                tableNumbers,
                staffName,
                adultUnitPrice,
                childUnitPrice,
                finePerKg,
                orderItems))
            {
                byte[] imageBytes = ConvertBitmapToEscPosRaster(receiptBitmap);

                string ipAddress = "127.0.0.1";
                int port = 9100;

                try
                {
                    using (TcpClient client = new TcpClient(ipAddress, port))
                    using (NetworkStream stream = client.GetStream())
                    {
                        List<byte> bytes = new List<byte>();

                        bytes.AddRange(new byte[] { 0x1B, 0x40 }); // Reset
                        bytes.AddRange(imageBytes); // Image
                        bytes.AddRange(new byte[] { 0x1B, 0x64, 0x03 }); // Feed
                        bytes.AddRange(new byte[] { 0x1D, 0x56, 0x42, 0x00 }); // Cut

                        byte[] data = bytes.ToArray();
                        await stream.WriteAsync(data, 0, data.Length);

                        return Ok(new { success = true, message = "สั่งพิมพ์ใบเสร็จเรียบร้อยแล้ว (Image Mode)" });
                    }
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, message = $"เกิดข้อผิดพลาด: {ex.Message}" });
                }
            }
        }

        private SKBitmap DrawReceiptImage(
            string resName,
            Bill bill,
            string tables,
            string staff,
            decimal adultPrice,
            decimal childPrice,
            decimal finePerKg,
            List<OrderItemDto> items)
        {
            int width = 576;
            int estimatedHeight = 1200;

            SKBitmap bitmap = new SKBitmap(width, estimatedHeight);
            using (SKCanvas canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);

                SKTypeface typeface = SKTypeface.FromFamilyName("Tahoma", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                     ?? SKTypeface.Default;
                SKTypeface boldTypeface = SKTypeface.FromFamilyName("Tahoma", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                     ?? SKTypeface.Default;

                using SKFont fontNormal = new SKFont(typeface, 22);
                using SKFont fontBold = new SKFont(boldTypeface, 24);
                using SKFont fontHeader = new SKFont(boldTypeface, 36);
                using SKFont fontTotal = new SKFont(boldTypeface, 32);

                using SKPaint paint = new SKPaint
                {
                    Color = SKColors.Black,
                    IsAntialias = true
                };

                float y = 50;
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

                void DrawRow(string left, string right, SKFont? font = null, float lineSpacing = 38)
                {
                    SKFont currentFont = font ?? fontNormal;
                    DrawTextLeft(left, leftMargin, y, currentFont);
                    DrawTextRight(right, rightMargin, y, currentFont);
                    y += lineSpacing;
                }

                void DrawDivider()
                {
                    using SKPaint linePaint = new SKPaint
                    {
                        Color = SKColors.Gray,
                        StrokeWidth = 2,
                        PathEffect = SKPathEffect.CreateDash(new float[] { 8, 4 }, 0)
                    };
                    canvas.DrawLine(leftMargin, y - 10, rightMargin, y - 10, linePaint);
                    y += 15;
                }

                // --- HEADER ---
                DrawTextCenter(resName, y, fontHeader);
                y += 50;


                // --- INFO ---
                DrawRow("เลขที่ใบเสร็จ:", $"{bill.Bill_id:D5}");
                DrawRow("วันที่เวลาเปิดบิล:", bill.Created_at.ToString("dd/MM/yyyy HH:mm:ss"));
                DrawRow("วันที่เวลาปิดบิล:", bill.Closed_at?.ToString("dd/MM/yyyy HH:mm:ss") ?? "ยังไม่ได้ปิด");
                DrawRow("โต๊ะ:", tables);

                DrawDivider();
                y += 10;
                // --- ITEMS ---
                if (bill.NumAdults > 0)
                {
                    decimal total = bill.NumAdults * adultPrice;
                    DrawRow($"ผู้ใหญ่ {bill.NumAdults} คน", $"{total:N0}");
                }
                if (bill.NumChildren > 0)
                {
                    decimal total = bill.NumChildren * childPrice;
                    DrawRow($"เด็ก {bill.NumChildren} คน", $"{total:N0}");
                }
                foreach (var item in items)
                {
                    decimal total = item.Quantity * item.Price;
                    DrawRow($"{item.MenuName} x{item.Quantity}", $"{total:N0}");
                }

                DrawDivider();
                y += 10;
                // --- SUMMARY ---
                decimal fineTotal = bill.Fine * finePerKg;
                DrawRow("ค่าปรับ:", $"{fineTotal:N0}");
                DrawRow("โปรโมชั่น:", bill.Discount_id.HasValue ? "มีส่วนลด" : "ไม่มี");

                DrawDivider();
                y += 15;
                // --- GRAND TOTAL ---
                DrawRow("รวมทั้งสิ้น:", $"{bill.Total_amount:N0} ฿", fontTotal, 45);


                // --- PAYMENT ---
                DrawRow("ชำระโดย:", !string.IsNullOrEmpty(bill.PaymentMethod) ? bill.PaymentMethod : "เงินสด");
                DrawRow("ชื่อพนักงาน:", staff);

                y += 50;

                // --- FOOTER ---
                DrawTextCenter("ขอบคุณที่ใช้บริการ", y, fontNormal);
                y += 35;
                DrawTextCenter("โปรดเก็บใบเสร็จไว้เป็นหลักฐาน", y, fontNormal);
                y += 20;

                int finalHeight = (int)y;
                SKBitmap croppedBitmap = new SKBitmap(width, finalHeight);
                using (SKCanvas cropCanvas = new SKCanvas(croppedBitmap))
                {
                    SKRect srcRect = new SKRect(0, 0, width, finalHeight);
                    SKRect destRect = new SKRect(0, 0, width, finalHeight);
                    cropCanvas.DrawBitmap(bitmap, srcRect, destRect, SKSamplingOptions.Default, paint);
                }

                return croppedBitmap;
            }
        }

        private byte[] ConvertBitmapToEscPosRaster(SKBitmap bitmap)
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

    public class OrderItemDto
    {
        public string MenuName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }
}