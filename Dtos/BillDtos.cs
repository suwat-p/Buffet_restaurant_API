using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Buffet_Restaurant_API.Dtos
{
    public class CreateWalkInBillDto
    {
        public int Config_id { get; set; }
        public List<int> Table_ids { get; set; } = new();
        public int Emp_id { get; set; }
        public int NumAdults { get; set; }
        public int NumChildren { get; set; }
        public int? Discount_id { get; set; } // เพิ่มเข้ามาตอนเปิดบิล Walk-in
    }

    public class CreateBookingBillDto
    {
        public int Config_id { get; set; }
        public int Emp_id { get; set; }
        public int Discount_id { get; set; } // เพิ่มเข้ามาตอนยืนยันลูกค้าจองหน้าร้าน
    }

    public class CloseBillDto
    {
        [Key]
        public int Bill_id { get; set; }

        [Column("closed_at")] // 🟢 ระบุชื่อคอลัมน์ใน DB ให้ชัดเจน
        public DateTime? Closed_at { get; set; }

        [Column("total_amount")]
        public decimal Total_amount { get; set; }

        [Column("payment_method")]
        public string? PaymentMethod { get; set; }

        [Column("emp_id")]
        public decimal Fine { get; set; }

        public int? Discount_id { get; set; }
        public int Emp_id { get; set; }
    }
    public class UpdateBillDto
    {
        public int Bill_id { get; set; }
        public decimal Fine { get; set; }
        public int NumAdults { get; set; }
        public int NumChildren { get; set; }
        public int? Discount_id { get; set; }
    }

    public class UpdatePaymentMethodDto
    {
        public string PaymentMethod { get; set; } = string.Empty;
        public string? TransactionId { get; set; }
    }
    public class updateFineDto
    {
        public decimal Fine { get; set; }
    }
}