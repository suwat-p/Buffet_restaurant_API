using Buffet_Restaurant_API.Dtos;
using Buffet_Restaurant_Managment_System_API.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Buffet_Restaurant_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DashboardController : ControllerBase
    {
        private readonly restaurantDbContext _context;

        public DashboardController(restaurantDbContext context)
        {
            _context = context;
        }

        [HttpGet("overview")]
        public async Task<ActionResult<DashboardOverviewDto>> GetOverview()
        {
            try
            {
                using var connection = _context.Database.GetDbConnection();

                // 🟢 1. ตรวจสอบและเปิด Connection หากยังปิดอยู่
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                // 🟢 2. SQL ยอดขายวันนี้
                var sqlTodaySales = @"
            SELECT COALESCE(SUM(Amount), 0) 
            FROM Payment 
            WHERE DATE(PaymentDateTime) = CURDATE()";

                // 🟢 3. SQL ลูกค้าทั้งหมดวันนี้
                var sqlTotalCustomers = @"
            SELECT COALESCE(SUM(NumAdults + NumChildren), 0) 
            FROM bill 
            WHERE DATE(Created_at) = CURDATE()";

                // 🟢 4. SQL ช่วงเวลาคึกคักที่สุด (ปรับ GROUP BY ให้รองรับ sql_mode)
                var sqlPeakTime = @"
            SELECT 
                CONCAT(LPAD(HOUR(Created_at), 2, '0'), ':00-', LPAD(HOUR(Created_at) + 1, 2, '0'), ':00') AS PeakTimeSlot,
                COALESCE(SUM(NumAdults + NumChildren), 0) AS PeakTimeCustomers
            FROM bill
            WHERE DATE(Created_at) = CURDATE()
            GROUP BY HOUR(Created_at), CONCAT(LPAD(HOUR(Created_at), 2, '0'), ':00-', LPAD(HOUR(Created_at) + 1, 2, '0'), ':00')
            ORDER BY PeakTimeCustomers DESC
            LIMIT 1";

                // ดึงข้อมูลดิบแล้วใช้ Convert เพื่อแปลงชนิดข้อมูล MySQL อย่างปลอดภัย
                var rawSales = await connection.ExecuteScalarAsync(sqlTodaySales);
                var rawCustomers = await connection.ExecuteScalarAsync(sqlTotalCustomers);
                var peakTimeData = await connection.QueryFirstOrDefaultAsync<PeakTimeResult>(sqlPeakTime);

                var result = new DashboardOverviewDto
                {
                    TodaySales = rawSales != null ? Convert.ToDecimal(rawSales) : 0m,
                    TotalCustomersToday = rawCustomers != null ? Convert.ToInt32(rawCustomers) : 0,
                    PeakTimeSlot = peakTimeData?.PeakTimeSlot ?? "-",
                    PeakTimeCustomers = peakTimeData?.PeakTimeCustomers != null ? Convert.ToInt32(peakTimeData.PeakTimeCustomers) : 0
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                // พิมพ์ข้อผิดพลาดลงใน Console ของ C# สำหรับตรวจสอบ
                Console.WriteLine($"[Dashboard Overview Error]: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // GET: api/dashboard/sales-chart?type=daily
        // GET: api/dashboard/sales-chart?type=daily&selectedDate=2026-08-26
        [HttpGet("sales-chart")]
        public async Task<ActionResult<SalesChartResponseDto>> GetSalesChart(
            [FromQuery] string type = "daily",
            [FromQuery] DateTime? selectedDate = null)
        {
            using var connection = _context.Database.GetDbConnection();
            string sqlQuery = string.Empty;
            var targetDate = selectedDate ?? DateTime.Today;

            switch (type.ToLower())
            {
                case "monthly":
                    // 🎯 เลือกเดือนไหน ให้ย้อนหลังไป 12 เดือน สิ้นสุด ณ เดือนที่เลือก
                    sqlQuery = @"
                SELECT 
                    DATE_FORMAT(PaymentDateTime, '%m/%Y') AS Label,
                    SUM(Amount) AS Amount
                FROM Payment
                WHERE PaymentDateTime >= DATE_SUB(LAST_DAY(@TargetDate), INTERVAL 12 MONTH) + INTERVAL 1 DAY
                  AND PaymentDateTime <= LAST_DAY(@TargetDate)
                GROUP BY DATE_FORMAT(PaymentDateTime, '%m/%Y'), YEAR(PaymentDateTime), MONTH(PaymentDateTime)
                ORDER BY YEAR(PaymentDateTime) ASC, MONTH(PaymentDateTime) ASC";
                    break;

                case "yearly":
                    // 🎯 เลือกปีไหน ให้ย้อนหลังไป 5 ปี สิ้นสุด ณ ปีที่เลือก
                    sqlQuery = @"
                SELECT 
                    DATE_FORMAT(PaymentDateTime, '%Y') AS Label,
                    SUM(Amount) AS Amount
                FROM Payment
                WHERE YEAR(PaymentDateTime) BETWEEN YEAR(@TargetDate) - 4 AND YEAR(@TargetDate)
                GROUP BY DATE_FORMAT(PaymentDateTime, '%Y'), YEAR(PaymentDateTime)
                ORDER BY YEAR(PaymentDateTime) ASC";
                    break;

                case "daily":
                default:
                    type = "daily";
                    // 🎯 เลือกวันไหน ให้ย้อนหลังไป 7 วัน สิ้นสุด ณ วันที่เลือก
                    sqlQuery = @"
                SELECT 
                    DATE_FORMAT(PaymentDateTime, '%d/%m/%Y') AS Label,
                    SUM(Amount) AS Amount
                FROM Payment
                WHERE DATE(PaymentDateTime) BETWEEN DATE_SUB(DATE(@TargetDate), INTERVAL 6 DAY) AND DATE(@TargetDate)
                GROUP BY DATE_FORMAT(PaymentDateTime, '%d/%m/%Y'), DATE(PaymentDateTime)
                ORDER BY DATE(PaymentDateTime) ASC";
                    break;
            }

            var chartData = (await connection.QueryAsync<SalesChartItemDto>(sqlQuery, new { TargetDate = targetDate })).ToList();

            return Ok(new SalesChartResponseDto
            {
                Type = type,
                Data = chartData
            });
        }
        [HttpGet("cashier-stats")]
        public async Task<ActionResult<CashierDashboardStatsDto>> GetCashierDashboardStats([FromQuery] string type = "daily")
        {
            try
            {
                using var connection = _context.Database.GetDbConnection();

                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                // 🟢 1. กำหนดเงื่อนไขเวลาตามปุ่ม (รายวัน, รายเดือน, รายปี)
                string paymentDateCondition = type.ToLower() switch
                {
                    "monthly" => "YEAR(PaymentDateTime) = YEAR(CURDATE()) AND MONTH(PaymentDateTime) = MONTH(CURDATE())",
                    "yearly" => "YEAR(PaymentDateTime) = YEAR(CURDATE())",
                    _ => "DATE(PaymentDateTime) = CURDATE()" // daily เป็น default
                };

                // เพิ่ม Alias 'b.' ให้กับ Created_at เพื่อระบุว่าเป็นคอลัมน์ของตาราง bill ป้องกันความสับสนเมื่อใช้ JOIN
                string billDateCondition = type.ToLower() switch
                {
                    "monthly" => "YEAR(b.Created_at) = YEAR(CURDATE()) AND MONTH(b.Created_at) = MONTH(CURDATE())",
                    "yearly" => "YEAR(b.Created_at) = YEAR(CURDATE())",
                    _ => "DATE(b.Created_at) = CURDATE()"
                };

                // 🟢 2. ดึงข้อมูลรายรับสุทธิ, เงินสด, โอน จากตาราง Payment
                var sqlPayment = $@"
                SELECT 
                    COALESCE(SUM(Amount), 0) AS NetRevenue,
                    COALESCE(SUM(CASE WHEN PaymentMethod = 'เงินสด' THEN Amount ELSE 0 END), 0) AS CashAmount,
                    COALESCE(SUM(CASE WHEN PaymentMethod = 'โอน' THEN Amount ELSE 0 END), 0) AS TransferAmount
                FROM Payment
                WHERE {paymentDateCondition}";

                // 🟢 3. ดึงข้อมูลจำนวนลูกค้า, ค่าปรับ และ ส่วนลด (JOIN ตาราง Discount)
                var sqlBill = $@"
                SELECT 
                    COALESCE(SUM(b.NumAdults), 0) AS TotalAdults,
                    COALESCE(SUM(b.NumChildren), 0) AS TotalChildren,
                    COALESCE(SUM(b.Fine), 0) AS TotalFines,
                    COALESCE(SUM(d.Discount_amount), 0) AS TotalDiscount
                FROM bill b
                LEFT JOIN Discount d ON b.Discount_id = d.Discount_id
                WHERE {billDateCondition}";

                // Execute Queries
                var paymentStats = await connection.QueryFirstOrDefaultAsync(sqlPayment);
                var billStats = await connection.QueryFirstOrDefaultAsync(sqlBill);

                // 🟢 4. แมปข้อมูลใส่ DTO เพื่อส่งกลับไปยังหน้าเว็บ
                var result = new CashierDashboardStatsDto
                {
                    NetRevenue = paymentStats?.NetRevenue != null ? Convert.ToDecimal(paymentStats.NetRevenue) : 0m,
                    CashAmount = paymentStats?.CashAmount != null ? Convert.ToDecimal(paymentStats.CashAmount) : 0m,
                    TransferAmount = paymentStats?.TransferAmount != null ? Convert.ToDecimal(paymentStats.TransferAmount) : 0m,

                    TotalAdults = billStats?.TotalAdults != null ? Convert.ToInt32(billStats.TotalAdults) : 0,
                    TotalChildren = billStats?.TotalChildren != null ? Convert.ToInt32(billStats.TotalChildren) : 0,
                    TotalFines = billStats?.TotalFines != null ? Convert.ToDecimal(billStats.TotalFines) : 0m,

                    // เพิ่มการส่งค่าส่วนลดที่ได้จากการ JOIN
                    TotalDiscount = billStats?.TotalDiscount != null ? Convert.ToDecimal(billStats.TotalDiscount) : 0m
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cashier Dashboard Stats Error]: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // GET: api/dashboard/hourly-chart?selectedDate=2026-10-09
        [HttpGet("hourly-chart")]
        public async Task<IActionResult> GetHourlyChart([FromQuery] DateTime? selectedDate = null)
        {
            try
            {
                using var connection = _context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                var targetDate = selectedDate ?? DateTime.Today;

                // SQL ดึงยอดขาย, จำนวนผู้ใหญ่, จำนวนเด็ก แยกรายชั่วโมง
                string sqlQuery = @"
            SELECT 
                CONCAT(LPAD(HOUR(b.Created_at), 2, '0'), ':00-', LPAD(HOUR(b.Created_at) + 1, 2, '0'), ':00') AS TimeSlot,
                HOUR(b.Created_at) AS Hour,
                COALESCE(SUM(p.Amount), 0) AS Sales,
                COALESCE(SUM(b.NumAdults), 0) AS Adults,
                COALESCE(SUM(b.NumChildren), 0) AS Children
            FROM bill b
            LEFT JOIN Payment p ON b.Bill_id = p.Bill_id
            WHERE DATE(b.Created_at) = DATE(@TargetDate)
            GROUP BY HOUR(b.Created_at), CONCAT(LPAD(HOUR(b.Created_at), 2, '0'), ':00-', LPAD(HOUR(b.Created_at) + 1, 2, '0'), ':00')
            ORDER BY Hour ASC";

                var rawData = await connection.QueryAsync(sqlQuery, new { TargetDate = targetDate });

                return Ok(rawData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hourly Chart Error]: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}