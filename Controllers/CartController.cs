using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Buffet_Restaurant_API.Models;
using Buffet_Restaurant_API.Dtos;
using Buffet_Restaurant_Managment_System_API.Data;

namespace Buffet_Restaurant_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CartController : ControllerBase
    {
        private readonly restaurantDbContext _context;

        public CartController(restaurantDbContext context)
        {
            _context = context;
        }

        // 1. เพิ่ม/ลด/ลบ จำนวน รายการในตะกร้า (รองรับทั้ง TableId และ BookingId)
        [HttpPost("add-item")]
        public async Task<IActionResult> AddItemToCart([FromBody] AddToCartDtos request)
        {
            if ((request.TableId ?? 0) <= 0 && (request.BookingId ?? 0) <= 0)
            {
                return BadRequest(new { message = "กรุณาระบุ TableId หรือ BookingId" });
            }
            if (request.TableId == null && request.BookingId == null)
            {
                return BadRequest(new { message = "กรุณาระบุ TableId หรือ BookingId" });
            }

            // ค้นหา Cart ตามเงื่อนไข (ถ้ามี BookingId ให้ค้นด้วย BookingId ถ้าไม่มีให้ใช้ TableId)
            IQueryable<Cart> cartQuery = _context.Carts.AsQueryable();

            if (request.BookingId.HasValue && request.BookingId.Value > 0)
            {
                cartQuery = cartQuery.Where(c => c.Booking_id == request.BookingId);
            }
            else
            {
                cartQuery = cartQuery.Where(c => c.Table_id == request.TableId);
            }

            var cart = await cartQuery.OrderByDescending(c => c.Created_at).FirstOrDefaultAsync();

            // หากยังไม่มีตะกร้า ให้สร้างใหม่
            if (cart == null)
            {
                cart = new Cart
                {
                    Table_id = request.BookingId.HasValue ? null : request.TableId,
                    Booking_id = request.BookingId,
                    Created_at = DateTime.Now
                };
                _context.Carts.Add(cart);
                await _context.SaveChangesAsync();
            }

            // จัดการรายการอาหารใน Cart
            var cartItem = await _context.CartItems
                                         .FirstOrDefaultAsync(ci => ci.Cart_id == cart.Cart_id && ci.Menu_id == request.MenuId);

            if (cartItem != null)
            {
                cartItem.Quantity += request.Quantity;

                if (cartItem.Quantity <= 0)
                {
                    _context.CartItems.Remove(cartItem);
                }
                else
                {
                    _context.CartItems.Update(cartItem);
                }
            }
            else
            {
                if (request.Quantity > 0)
                {
                    cartItem = new Cart_item
                    {
                        Cart_id = cart.Cart_id,
                        Menu_id = request.MenuId,
                        Quantity = request.Quantity
                    };
                    await _context.CartItems.AddAsync(cartItem);
                }
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "อัปเดตรายการสำเร็จ", cartId = cart.Cart_id });
        }

        // 2. ลบรายการชิ้นนั้นๆ ออกจากตะกร้า
        [HttpDelete("delete-item/{cartItemId}")]
        public async Task<IActionResult> DeleteItem(int cartItemId)
        {
            var item = await _context.CartItems.FindAsync(cartItemId);
            if (item == null) return NotFound(new { message = "ไม่พบรายการ" });

            _context.CartItems.Remove(item);
            await _context.SaveChangesAsync();

            return Ok(new { message = "ลบรายการเรียบร้อย" });
        }

        // 3. ดึงตะกร้าสำหรับ "สั่งหน้าร้าน" (ใช้ TableId)
        [HttpGet("get-items/{tableId}")]
        public async Task<IActionResult> GetCartItems(int tableId)
        {
            var cart = await _context.Carts
                                     .Where(c => c.Table_id == tableId)
                                     .OrderByDescending(c => c.Created_at)
                                     .FirstOrDefaultAsync();

            if (cart == null) return Ok(new { cartId = 0, items = new List<object>() });

            return await GetCartResponse(cart.Cart_id);
        }

        // 4. 🟢 เพิ่ม Endpoint: ดึงตะกร้าสำหรับ "สั่งล่วงหน้า Pre-order" (ใช้ BookingId)
        [HttpGet("get-items-by-booking/{bookingId}")]
        public async Task<IActionResult> GetCartItemsByBooking(int bookingId)
        {
            var cart = await _context.Carts
                                     .Where(c => c.Booking_id == bookingId)
                                     .OrderByDescending(c => c.Created_at)
                                     .FirstOrDefaultAsync();

            if (cart == null) return Ok(new { cartId = 0, items = new List<object>() });

            return await GetCartResponse(cart.Cart_id);
        }

        // Helper Method สำหรับ Map ข้อมูลรายการสินค้าใน Cart
        private async Task<IActionResult> GetCartResponse(int cartId)
        {
            var items = await _context.CartItems
                                      .Where(ci => ci.Cart_id == cartId)
                                      .Join(_context.Menus,
                                            ci => ci.Menu_id,
                                            m => m.Menu_id,
                                            (ci, m) => new
                                            {
                                                id = ci.Cartitem_id,
                                                menuId = m.Menu_id,
                                                name = m.Menu_Name,
                                                price = m.Price ?? 0,
                                                quantity = ci.Quantity,
                                                image = m.Menu_Image,
                                                selected = true
                                            })
                                      .ToListAsync();

            return Ok(new { cartId = cartId, items = items });
        }
    }
}