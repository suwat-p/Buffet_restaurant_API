using System.Text.Json;
using Buffet_Restaurant_API.Dtos;
using Buffet_Restaurant_Managment_System_API.Data;
using Buffet_Restaurant_Managment_System_API.Hubs;
using Buffet_Restaurant_Managment_System_API.Models;
using Buffet_Restaurant_Managment_System_API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Buffet_Restaurant_Managment_System_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PaymentController : ControllerBase
    {
        private readonly PromptPayService _promptPayService;
        private readonly restaurantDbContext _context;
        private readonly IHubContext<tableStatusHub> _hubContext;

        public PaymentController(
            PromptPayService promptPayService,
            restaurantDbContext context,
            IHubContext<tableStatusHub> hubContext
        )
        {
            _promptPayService = promptPayService;
            _context = context;
            _hubContext = hubContext;
        }

        [HttpPost("generate-qr")]
        public async Task<IActionResult> CreateQr([FromBody] QrRequestDto request)
        {
            var booking = await _context.Bookings
                .FirstOrDefaultAsync(b => b.Booking_id == request.BookingId);

            if (booking == null)
            {
                return NotFound(new { message = "ไม่พบข้อมูลการจอง" });
            }

            decimal amountToPay = booking.Deposit_Amount;

            var qrResult = await _promptPayService.GeneratePromptPayQr(amountToPay);

            Console.WriteLine($"=== QR RESULT: {qrResult} ===");

            if (string.IsNullOrWhiteSpace(qrResult) || qrResult.StartsWith("Error"))
            {
                return BadRequest(new
                {
                    message = "สร้าง QR Code ไม่สำเร็จ (ติดปัญหาการยืนยันตัวตนกับระบบชำระเงิน)",
                    detail = qrResult
                });
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(qrResult);
                var transactionId = parsed.GetProperty("data").GetProperty("transactionId").GetString();


                var amountStr = parsed.GetProperty("data").GetProperty("amount").GetString();

                return Ok(new
                {
                    qr_data = qrResult,
                    amount_pay = amountStr,          // ยอดที่ต้องโอนจริงตาม QR (มีเศษสุ่มเพื่อ track)
                    deposit_amount = booking.Deposit_Amount, // ยอดมัดจำจริงตามราคาที่ตั้งไว้ (อ้างอิง)
                    booking_id = booking.Booking_id,
                    transaction_id = transactionId,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "อ่านข้อมูล QR Code ไม่สำเร็จ", detail = ex.Message, rawData = qrResult });
            }
        }

        [HttpPost("check-status")]
        public async Task<IActionResult> CheckStatus([FromBody] string transactionId)
        {
            if (string.IsNullOrEmpty(transactionId))
                return BadRequest("Transaction ID is required");

            var result = await _promptPayService.CheckPaymentStatus(transactionId);
            return Ok(result);
        }

        [HttpPost("generate-checkout-qr")]
        public async Task<IActionResult> CreateCheckoutQr([FromBody] CheckoutQrRequestDto request)
        {
            var bill = await _context.Bill
                .FirstOrDefaultAsync(b => b.Bill_id == request.BillId);

            if (bill == null)
            {
                return NotFound(new { message = "ไม่พบข้อมูลบิลที่ต้องการชำระเงิน" });
            }

            decimal amountToPay = bill.Total_amount;

            var qrResult = await _promptPayService.GeneratePromptPayQr(amountToPay);
            Console.WriteLine($"=== CHECKOUT QR RESULT: {qrResult} ===");

            if (string.IsNullOrWhiteSpace(qrResult) || qrResult.StartsWith("Error"))
            {
                return BadRequest(new
                {
                    message = "สร้าง QR Code ไม่สำเร็จ (ติดปัญหาการยืนยันตัวตนกับระบบชำระเงิน)",
                    detail = qrResult
                });
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(qrResult);
                var dataProp = parsed.GetProperty("data");

                var transactionId = dataProp.GetProperty("transactionId").GetString();

                var amountProp = dataProp.GetProperty("amount");
                string amountStr = amountProp.ValueKind == JsonValueKind.Number
                    ? amountProp.GetDecimal().ToString("F2")
                    : amountProp.GetString();

                // 🟢 เปลี่ยนมาใช้ยอดเงินจริงจาก QR (เช่น 1.02) อัปเดตลง Bill DB
                if (decimal.TryParse(amountStr, out decimal actualAmount))
                {
                    bill.Total_amount = actualAmount;
                }
                else
                {
                    bill.Total_amount = amountToPay;
                }

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    qr_data = qrResult,
                    amount_pay = amountStr, // 🟢 ส่ง 1.02 ตรงตามธนาคารกลับไปหน้าบ้าน
                    bill_id = bill.Bill_id,
                    booking_id = bill.Booking_id,
                    transaction_id = transactionId
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "อ่านข้อมูล QR Code ไม่สำเร็จ", detail = ex.Message, rawData = qrResult });
            }
        }

        [HttpPost("verify-payment")]
        public async Task<IActionResult> VerifyPayment([FromBody] VerifyPaymentRequestDto request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var bill = await _context.Bill
                    .FirstOrDefaultAsync(b => b.Bill_id == request.BillId);

                if (bill == null)
                {
                    return NotFound(new { message = "ไม่พบข้อมูลบิล" });
                }

                // ตรวจสอบสถานะชำระเงินจาก PromptPay / Bank Gateway API
                var result = await _promptPayService.CheckPaymentStatus(request.TransactionId);

                bool isPaid = false;

                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonElement>(result);

                    if (parsed.TryGetProperty("status", out var statusProp))
                    {
                        var statusValue = statusProp.GetString()?.Trim().ToLower();
                        if (statusValue == "success")
                        {
                            isPaid = true;
                        }
                    }
                }
                catch
                {
                    if (result?.Trim().ToLower() == "success")
                    {
                        isPaid = true;
                    }
                }

                if (!isPaid)
                {
                    return Ok(new
                    {
                        status = "pending",
                        message = "ยังไม่ได้ชำระเงิน",
                        debug_result = result
                    });
                }

                // 2. บันทึกข้อมูลการชำระเงินลงตาราง Payment
                var paymentRecord = new Payment
                {
                    Booking_id = bill.Booking_id,
                    Bill_id = bill.Bill_id,
                    Amount = bill.Total_amount,
                    PaymentMethod = "โอน",
                    Payment_Type = "1", // 'หน้าร้าน'
                    PaymentDateTime = DateTime.Now,
                    TransactionId = request.TransactionId
                };

                _context.Payment.Add(paymentRecord);

                // 3. อัปเดตสถานะบิล
                bill.PaymentMethod = "โอน";
                bill.Closed_at = DateTime.Now;

                var tableIds = new List<int>();

                if (bill.Booking_id.HasValue)
                {
                    var booking = await _context.Bookings
                        .Include(b => b.GroupTables)
                        .FirstOrDefaultAsync(b => b.Booking_id == bill.Booking_id.Value);

                    if (booking != null)
                    {
                        tableIds = booking.GroupTables
                            .Where(gt => gt.Table_id.HasValue)
                            .Select(gt => gt.Table_id!.Value)
                            .ToList();
                    }
                }
                else
                {
                    tableIds = await _context.GroupTables
                        .Where(gt => gt.Bill_id == bill.Bill_id && gt.Table_id.HasValue)
                        .Select(gt => gt.Table_id!.Value)
                        .ToListAsync();
                }

                var tables = await _context.Tables
                    .Where(t => tableIds.Contains(t.Table_id))
                    .ToListAsync();

                tables.ForEach(t => t.Table_Status = "ว่าง");

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                await _hubContext.Clients.All.SendAsync("UpdateBill", new
                {
                    billId = bill.Bill_id,
                    status = "CLOSED",
                    paymentId = paymentRecord.Payment_Id
                });

                foreach (var table in tables)
                {
                    await _hubContext.Clients.All.SendAsync("UpdateTable", new
                    {
                        tableId = table.Table_id,
                        status = "ว่าง"
                    });
                }

                return Ok(new
                {
                    status = "success",
                    api_result = result,
                    message = "ชำระเงินสำเร็จ และปิดบิลเรียบร้อยแล้ว",
                    payment_id = paymentRecord.Payment_Id,
                    bill_id = bill.Bill_id
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("update-payment-method/{billId}")]
        public async Task<IActionResult> UpdatePaymentMethod(int billId, [FromBody] UpdatePaymentMethodDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.PaymentMethod))
            {
                return BadRequest(new { message = "กรุณาระบุประเภทการชำระเงิน" });
            }

            try
            {
                var bill = await _context.Bill.FirstOrDefaultAsync(b => b.Bill_id == billId);

                if (bill == null)
                {
                    return NotFound(new { message = "ไม่พบข้อมูลบิลที่ต้องการแก้ไข" });
                }

                bill.PaymentMethod = dto.PaymentMethod;

                var payment = await _context.Payment.FirstOrDefaultAsync(p => p.Bill_id == billId);
                if (payment != null)
                {
                    payment.PaymentMethod = dto.PaymentMethod;

                    if (!string.IsNullOrEmpty(dto.TransactionId))
                    {
                        payment.TransactionId = dto.TransactionId;
                    }
                }

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "อัปเดตประเภทการชำระเงินเรียบร้อยแล้ว",
                    bill_id = bill.Bill_id,
                    paymentMethod = bill.PaymentMethod
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "เกิดข้อผิดพลาดในการอัปเดตประเภทการชำระเงิน", error = ex.Message });
            }
        }
    }
}