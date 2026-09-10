using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Buffet_Restaurant_API.Models;
using Buffet_Restaurant_API.Dtos;
using Buffet_Restaurant_Managment_System_API.Data;
using Buffet_Restaurant_Managment_System_API.Hubs;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using QRCoder;

namespace Buffet_Restaurant_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BookingController : ControllerBase
    {
        private readonly restaurantDbContext _context;
        private readonly IHubContext<tableStatusHub> _hubContext;
        private readonly Cloudinary _cloudinary;

        // ราคาต่อคน (บาท) — ปรับให้ตรงราคาจริงของร้าน
        private const decimal PricePerAdult = 199m;
        private const decimal PricePerChild = 99m;

        public BookingController(
            restaurantDbContext context,
            IHubContext<tableStatusHub> hubContext,
            Cloudinary cloudinary)
        {
            _context = context;
            _hubContext = hubContext;
            _cloudinary = cloudinary;
        }


        [HttpGet("available-tables")]
        public async Task<IActionResult> GetAvailableTables()
        {
            var tables = await _context.Tables
                .Select(t => new { t.Table_id, t.Table_Number, t.Table_Status, t.Table_QR_Code })
                .ToListAsync();
            return Ok(tables);
        }

        [HttpGet("member/{memberId}")]
        public async Task<IActionResult> GetByMember(int memberId)
        {
            var bookings = await _context.Bookings
                .Include(b => b.Member)
                .Include(b => b.GroupTables).ThenInclude(gt => gt.Table)
                .Where(b => b.Member_id == memberId)
                .OrderByDescending(b => b.Booking_DateTime)
                .Select(b => new BookingResponseDto
                {
                    Booking_id = b.Booking_id,
                    Member_Name = b.Member!.Fullname,
                    Booking_DateTime = b.Booking_DateTime,
                    Booking_Status = b.Booking_Status,
                    Adult_Count = b.Adult_Count,
                    Child_Count = b.Child_Count,
                    Tables_Booked = b.GroupTables.Select(gt => gt.Table!.Table_Number).ToList(),
                    QR_Url = b.QR_Url
                })
                .ToListAsync();
            return Ok(bookings);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetBooking(int id)
        {
            var booking = await _context.Bookings
                .Include(b => b.Member)
                .Include(b => b.GroupTables).ThenInclude(gt => gt.Table)
                .FirstOrDefaultAsync(b => b.Booking_id == id);

            if (booking == null) return NotFound(new { message = "ไม่พบการจอง" });

            return Ok(new BookingResponseDto
            {
                Booking_id = booking.Booking_id,
                Member_Name = booking.Member?.Fullname ?? "",
                Member_Phone = booking.Member?.Phone ?? "",
                Booking_DateTime = booking.Booking_DateTime,
                Booking_Status = booking.Booking_Status,
                Adult_Count = booking.Adult_Count,
                Child_Count = booking.Child_Count,
                Tables_Booked = booking.GroupTables.Select(gt => gt.Table?.Table_Number ?? "").ToList()
            });
        }

        [HttpGet("checkin-info")]
        public async Task<IActionResult> GetCheckinInfo([FromQuery] int bookingId, [FromQuery] int tableId)
        {
            var booking = await _context.Bookings
                .Include(b => b.Member)
                .Include(b => b.GroupTables).ThenInclude(gt => gt.Table)
                .FirstOrDefaultAsync(b => b.Booking_id == bookingId);

            if (booking == null)
                return NotFound(new { message = "ไม่พบข้อมูลการจอง" });

            var targetTable = booking.GroupTables.FirstOrDefault(gt => gt.Table_id == tableId);
            if (targetTable == null)
                return NotFound(new { message = "โต๊ะนี้ไม่ได้อยู่ในการจองนี้" });

            return Ok(new
            {
                booking_id = booking.Booking_id,
                booking_status = booking.Booking_Status,
                booking_datetime = booking.Booking_DateTime,
                adult_count = booking.Adult_Count,
                child_count = booking.Child_Count,
                member = new
                {
                    name = booking.Member?.Fullname ?? "",
                    phone = booking.Member?.Phone ?? ""
                },
                table = new
                {
                    table_id = targetTable.Table?.Table_id,
                    table_number = targetTable.Table?.Table_Number ?? ""
                },
                all_tables = booking.GroupTables
                    .Select(gt => gt.Table?.Table_Number ?? "")
                    .Where(n => n != "")
                    .ToList()
            });
        }

        //  สร้างการจอง + คำนวณยอดมัดจำ
        [HttpPost("create")]
        public async Task<IActionResult> CreateBooking([FromBody] CreateBookingDto dto)
        {
            var availableTables = await _context.Tables
                .Where(t => dto.Table_ids.Contains(t.Table_id) && t.Table_Status == "ว่าง")
                .ToListAsync();

            if (availableTables.Count != dto.Table_ids.Count)
            {
                var unavailable = dto.Table_ids.Except(availableTables.Select(t => t.Table_id)).ToList();
                return Conflict(new { message = "บางโต๊ะไม่ว่าง", unavailable_table_ids = unavailable });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 🟢 1. ดึงการตั้งค่าราคาล่าสุดที่เจ้าของร้านตั้งไว้จาก Res_Config
                var resConfig = await _context.Res_Config.FirstOrDefaultAsync();

                // กำหนดราคาผู้ใหญ่/เด็กจาก DB (หากใน DB ไม่มีข้อมูล ให้ใช้ค่า Default สำรอง)
                decimal pricePerAdult = resConfig?.Price_Adult ?? 0m;
                decimal pricePerChild = resConfig?.Price_Child ?? 0m;

                // 🟢 2. คำนวณราคารวมจริง
                decimal total = (dto.Adult_Count * pricePerAdult) + (dto.Child_Count * pricePerChild);

                // 🟢 3. คำนวณยอดมัดจำ 50% จากราคารวม
                decimal deposit = total * 0.5m;

                var booking = new Booking
                {
                    Member_id = dto.Member_id,
                    Booking_DateTime = dto.Booking_DateTime,
                    Adult_Count = dto.Adult_Count,
                    Child_Count = dto.Child_Count,
                    Booking_Status = "Pending",
                    Deposit_Amount = deposit        // บันทึกยอดมัดจำจริงลง DB
                };
                _context.Bookings.Add(booking);
                await _context.SaveChangesAsync();

                var groupTables = dto.Table_ids.Select(tid => new GroupTable
                {
                    Booking_id = booking.Booking_id,
                    Table_id = tid
                }).ToList();
                _context.GroupTables.AddRange(groupTables);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return CreatedAtAction(nameof(GetBooking),
                    new { id = booking.Booking_id },
                    new
                    {
                        booking_id = booking.Booking_id,
                        tables = availableTables.Select(t => t.Table_Number),
                        price_per_adult = pricePerAdult,
                        price_per_child = pricePerChild,
                        total_amount = total,
                        deposit_amount = deposit,            // ส่งยอดมัดจำ 50% จริงให้ Frontend
                        remaining_amount = total - deposit   // ยอดคงเหลือไปจ่ายที่แคชเชียร์
                    });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPost("checkin")]
        public async Task<IActionResult> CheckIn([FromBody] CheckinDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 1. ค้นหาข้อมูลการจองพร้อม GroupTables
                var booking = await _context.Bookings
                    .Include(b => b.GroupTables)
                    .FirstOrDefaultAsync(b => b.Booking_id == dto.BookingId);

                if (booking == null)
                    return NotFound(new { message = "ไม่พบข้อมูลการจองนี้ในระบบ" });

                if (booking.Booking_Status != "Confirmed")
                    return BadRequest(new { message = $"ไม่สามารถเช็คอินได้ สถานะการจองคือ '{booking.Booking_Status}'" });

                // 🎯 2. ดึง Table_id ทั้งหมดจาก GroupTables ของ Booking นี้
                var allTableIds = booking.GroupTables
                    .Where(gt => gt.Table_id.HasValue)
                    .Select(gt => gt.Table_id!.Value)
                    .ToList();

                if (!allTableIds.Any())
                    return BadRequest(new { message = "ไม่พบข้อมูลโต๊ะที่ผูกกับการจองนี้" });

                // 3. เปลี่ยนสถานะโต๊ะทั้งหมดเป็น "ไม่ว่าง"
                var allTables = await _context.Tables
                    .Where(t => allTableIds.Contains(t.Table_id))
                    .ToListAsync();

                allTables.ForEach(t => t.Table_Status = "ไม่ว่าง");

                // 4. หา Bill ของการจองนี้: ถ้าเคยสั่งอาหารล่วงหน้าไว้จะมี Bill อยู่แล้ว (สร้างจาก PlaceOrder)
                //    ให้ใช้ใบเดิมต่อ ห้ามสร้างใหม่ซ้ำ ไม่งั้นออเดอร์ที่สั่งล่วงหน้าจะหลุดไปอยู่คนละบิลกับโต๊ะที่เช็คอิน
                var existingBill = await _context.Bill
                    .FirstOrDefaultAsync(b => b.Booking_id == booking.Booking_id);

                Bill autoBill;
                if (existingBill != null)
                {
                    autoBill = existingBill;
                    // อัปเดตข้อมูลบิลให้ตรงกับตอนเช็คอินจริง (เผื่อจำนวนคน/พนักงาน/ส่วนลดเปลี่ยนไปจากตอนสั่งล่วงหน้า)
                    autoBill.Emp_id = dto.Emp_id ?? autoBill.Emp_id;
                    autoBill.Discount_id = dto.Discount_id ?? autoBill.Discount_id;
                    autoBill.NumAdults = booking.Adult_Count;
                    autoBill.NumChildren = booking.Child_Count;
                }
                else
                {
                    autoBill = new Bill
                    {
                        Booking_id = booking.Booking_id,
                        Config_id = dto.Config_id ?? 1,
                        Emp_id = dto.Emp_id ?? 1,
                        Discount_id = dto.Discount_id,
                        Created_at = DateTime.Now,
                        NumAdults = booking.Adult_Count,
                        NumChildren = booking.Child_Count,
                        Fine = 0,
                        Total_amount = booking.Deposit_Amount,
                        PaymentMethod = null
                    };

                    _context.Bill.Add(autoBill);
                }

                await _context.SaveChangesAsync(); // บันทึกเพื่อให้ได้ autoBill.Bill_id

                // 🎯 5. หยอด Bill_id ใส่ใน GroupTables ทุกแถวของการจองนี้
                foreach (var gt in booking.GroupTables)
                {
                    gt.Bill_id = autoBill.Bill_id;
                }

                // 6. อัปเดตสถานะการจองเป็น Completed
                booking.Booking_Status = "Completed";

                // 🍳 7. ลูกค้ามาถึงแล้ว -> ออเดอร์ที่สั่งล่วงหน้าไว้ (สถานะยังเป็น "รับออเดอร์") ให้เริ่มเข้าครัวได้เลย
                var preOrdersToStart = await _context.Orders
                    .Where(o => o.Bill_id == autoBill.Bill_id && o.Order_Status == "รับออเดอร์")
                    .ToListAsync();

                foreach (var order in preOrdersToStart)
                {
                    order.Order_Status = "กำลังจัดเตรียมอาหาร";
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // 8. แจ้งเตือน Real-time ผ่าน SignalR
                foreach (var table in allTables)
                {
                    await _hubContext.Clients.All
                        .SendAsync("UpdateTable", new { tableId = table.Table_id, status = "ไม่ว่าง" });
                }

                // แจ้งครัวว่าออเดอร์ที่สั่งล่วงหน้าไว้เริ่มทำได้แล้ว (ลูกค้ามาถึงร้านแล้ว)
                foreach (var order in preOrdersToStart)
                {
                    await _hubContext.Clients.All.SendAsync("NewKitchenOrder", order.Order_id);

                    // 🟢 แจ้งหน้าติดตามสถานะของลูกค้า (StatusPreOrder) ว่าออเดอร์เปลี่ยนสถานะแล้ว
                    await _hubContext.Clients.All.SendAsync("OrderStatusUpdated", new
                    {
                        orderId = order.Order_id,
                        status = order.Order_Status, // "กำลังจัดเตรียมอาหาร"
                        billId = order.Bill_id
                    });
                }

                return Ok(new
                {
                    message = "เช็คอินและสร้างบิลจากการจองสำเร็จ",
                    booking_id = dto.BookingId,
                    bill_id = autoBill.Bill_id,
                    booking_status = booking.Booking_Status,
                    tables_checked_in = allTables.Select(t => t.Table_Number).ToList()
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "เกิดข้อผิดพลาดในการเช็คอิน", error = ex.Message });
            }
        }

        [HttpPatch("{id}/status")]
        public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateBookingStatusDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var booking = await _context.Bookings
                    .Include(b => b.GroupTables)
                    .FirstOrDefaultAsync(b => b.Booking_id == id);

                if (booking == null) return NotFound(new { message = "ไม่พบการจอง" });

                booking.Booking_Status = dto.Booking_Status;

                var tableIds = booking.GroupTables
                    .Where(gt => gt.Table_id.HasValue)
                    .Select(gt => gt.Table_id!.Value).ToList();

                var tables = await _context.Tables
                    .Where(t => tableIds.Contains(t.Table_id)).ToListAsync();

                if (dto.Booking_Status == "Confirmed")
                {
                    //  ปรับโต๊ะเป็นติดจอง
                    tables.ForEach(t => t.Table_Status = "ติดจอง");

                    //  เจน QR เช็คอิน
                    var qrGenerator = new QRCodeGenerator();
                    var qrData = qrGenerator.CreateQrCode(
                    $"https://buffet-restaurant-management-system.vercel.app/Checkin?bookingId={id}&tableId={tableIds.FirstOrDefault()}", QRCodeGenerator.ECCLevel.Q);
                    var qrCode = new PngByteQRCode(qrData);
                    var qrBytes = qrCode.GetGraphic(10);

                    using var ms = new MemoryStream(qrBytes);
                    var uploadParams = new ImageUploadParams
                    {
                        File = new FileDescription($"qr_{id}.png", ms),
                        Folder = "checkin_qr"
                    };
                    var uploadResult = await _cloudinary.UploadAsync(uploadParams);
                    booking.QR_Url = uploadResult.SecureUrl.ToString();

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    //  แจ้ง SignalR เรียลไทม์
                    foreach (var table in tables)
                        await _hubContext.Clients.All
                            .SendAsync("UpdateTable", new { tableId = table.Table_id, status = "ติดจอง" });

                    return Ok(new
                    {
                        message = "Confirmed สำเร็จ",
                        qr_url = booking.QR_Url
                    });
                }
                else if (dto.Booking_Status == "Cancelled")
                {
                    tables.ForEach(t => t.Table_Status = "ว่าง");
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    foreach (var table in tables)
                        await _hubContext.Clients.All
                            .SendAsync("UpdateTable", new { tableId = table.Table_id, status = "ว่าง" });
                }
                else
                {
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }

                return Ok(new { message = $"อัพเดตสถานะเป็น {dto.Booking_Status} สำเร็จ" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpPut("update/{id}")]
        public async Task<IActionResult> UpdateBooking(int id, [FromBody] UpdateBookingDto dto)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var booking = await _context.Bookings
                    .Include(b => b.GroupTables)
                    .FirstOrDefaultAsync(b => b.Booking_id == id);

                if (booking == null)
                    return NotFound(new { message = "ไม่พบการจอง" });

                if (booking.Booking_Status == "Completed" || booking.Booking_Status == "Cancelled")
                    return BadRequest(new { message = $"ไม่สามารถแก้ไขได้เนื่องจากสถานะปัจจุบันคือ '{booking.Booking_Status}'" });

                if (booking.Booking_DateTime != dto.Booking_DateTime)
                {
                    var currentTableIds = booking.GroupTables
                        .Where(gt => gt.Table_id.HasValue)
                        .Select(gt => gt.Table_id!.Value).ToList();

                    bool isTimeConflict = await _context.Bookings
                        .Include(b => b.GroupTables)
                        .AnyAsync(b =>
                            b.Booking_id != id &&
                            b.Booking_DateTime == dto.Booking_DateTime &&
                            b.Booking_Status != "Cancelled" &&
                            b.GroupTables.Any(gt => gt.Table_id.HasValue && currentTableIds.Contains(gt.Table_id.Value))
                        );

                    if (isTimeConflict)
                        return StatusCode(409, new { message = "เวลาใหม่ที่คุณเลือก มีคิวอื่นจองโต๊ะนี้ไปแล้ว" });

                    booking.Booking_DateTime = dto.Booking_DateTime;
                }

                booking.Adult_Count = dto.AdultCount;
                booking.Child_Count = dto.ChildCount;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { message = "อัปเดตข้อมูลการจองสำเร็จ", booking_id = booking.Booking_id });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> CancelBooking(int id)
        {
            return await UpdateStatus(id, new UpdateBookingStatusDto { Booking_Status = "Cancelled" });
        }
    }
}