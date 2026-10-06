using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Buffet_Restaurant_API.Models
{
    public class Bill
    {
        [Key]
        public int Bill_id {get;set;}
        public int? Booking_id {get;set;}
        public int Config_id {get;set;}
        public int Emp_id {get;set;}
        public int? Discount_id {get;set;}
        public DateTime Created_at {get; set;}
        public DateTime? Closed_at {get; set;}
        public int NumAdults {get; set;}
        public int NumChildren {get; set;}
        public decimal Fine {get; set;}
        public decimal Total_amount {get; set;}
        public string? PaymentMethod {get; set;}
        [ForeignKey("Booking_id")]
        public virtual Booking? Booking { get; set; }
    }
}