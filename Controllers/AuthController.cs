using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Buffet_Restaurant_Managment_System_API.Data;
using Buffet_Restaurant_Managment_System_API.Models;
using Buffet_Restaurant_Managment_System_API.Dtos;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Caching.Memory;
using MailKit.Net.Smtp;
using MimeKit;
namespace Buffet_Restaurant_Managment_System_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly restaurantDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;
        public AuthController(restaurantDbContext context, IConfiguration configuration, IMemoryCache cache)
        {
            _context = context;
            _configuration = configuration;
            _cache = cache;
        }

        [HttpPost("login-employee")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> LoginEmployee([FromForm] loginEmployeeDtos loginData)
        {
            var employee = await _context.Employee
                .FirstOrDefaultAsync(e => e.Phone == loginData.Phone);

            if (employee == null)
            {
                return Unauthorized(new { Message = "ไม่พบข้อมูลผู้ใช้งาน หรือเบอร์โทรศัพท์ไม่ถูกต้อง" });
            }
            try
            {
                if (!BCrypt.Net.BCrypt.Verify(loginData.Password, employee.Password))
                {
                    return Unauthorized(new { Message = "รหัสผ่านไม่ถูกต้อง" });
                }
            }
            catch (BCrypt.Net.SaltParseException)
            {
                return BadRequest(new { Message = "รูปแบบรหัสผ่านในระบบไม่ถูกต้อง (ไม่ใช่ BCrypt)" });
            }
            if (employee.Employee_Status != "ทำงานปัจจุบัน")
            {
                return Unauthorized(new { Message = "ยังไม่ได้รับการอนุมัติการเป็นพนักงาน" });
            }

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, employee.Emp_id.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, employee.Fullname?? "No Name"),
                new Claim("role", employee.Department?? "พนักงาน")
            };
            var jwtKey = _configuration["Jwt:Key"];
            if (string.IsNullOrEmpty(jwtKey))
            {
                return StatusCode(500, "ระบบผิดพลาด: ไม่พบการตั้งค่า Jwt:Key ใน appsettings.json");
            }
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(8),
                signingCredentials: creds);
            return Ok(new
            {
                Message = "เข้าสู่ระบบสำเร็จ",
                Token = new JwtSecurityTokenHandler().WriteToken(token),
                EmployeeName = employee.Fullname
            });
        }

        [HttpPost("login-member")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> LoginMember([FromForm] loginMemberDtos loginData)
        {
            var member = await _context.Members
                .FirstOrDefaultAsync(m => m.Phone == loginData.Phone);

            if (member == null || !BCrypt.Net.BCrypt.Verify(loginData.Password, member.Password))
            {
                return Unauthorized(new { Message = "เบอร์โทรศัพท์หรือรหัสผ่านไม่ถูกต้อง" });
            }
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, member.Member_id.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, member.Fullname),
                new Claim("role", "Member")
            };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(1),
                signingCredentials: creds);

            return Ok(new
            {
                Message = "เข้าสู่ระบบสำเร็จ",
                Token = new JwtSecurityTokenHandler().WriteToken(token),
                MemberName = member.Fullname
            });
        }
        [HttpPost("register-employee")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> registerEmployee([FromForm] resgisterEmployeeDtos employee, [FromServices] Cloudinary cloudinary)
        {
            if (await _context.Employee.AnyAsync(e => e.Phone == employee.Phone))
            {
                return BadRequest(new { Message = "เบอร์โทรศัพท์นี้ถูกใช้งานแล้ว" });
            }

            if (await _context.Employee.AnyAsync(e => e.Email == employee.Email))
            {
                return BadRequest(new { Message = "อีเมลนี้ถูกใช้งานแล้ว" });
            }

            if (await _context.Employee.AnyAsync(e => e.Identification_Number == employee.Identification_Number))
            {
                return BadRequest(new { Message = "เลขบัตรประชาชนนี้ถูกใช้งานแล้ว" });
            }
            string passwordHash = BCrypt.Net.BCrypt.HashPassword(employee.Password);

            var newEmployee = new Employee
            {
                Fullname = employee.Fullname,
                Email = employee.Email,
                Phone = employee.Phone,
                Password = passwordHash,
                Gender = employee.Gender,
                Identification_Number = employee.Identification_Number,
                Address = employee.Address,
                Image_Profile = "",
                Department = employee.Department,
                Employee_Type = employee.Employee_Type
            };

            if (employee.Image_Profile != null && employee.Image_Profile.Length > 0)
            {

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };
                var extension = Path.GetExtension(employee.Image_Profile.FileName).ToLower();
                if (!allowedExtensions.Contains(extension))
                    return BadRequest(new { message = "กรุณาอัปโหลดไฟล์รูปภาพ (.jpg, .png) เท่านั้น" });

                using var stream = employee.Image_Profile.OpenReadStream();
                var uploadParams = new ImageUploadParams()
                {
                    File = new FileDescription(employee.Image_Profile.FileName, stream),
                    Folder = "Employee_profiles",
                    PublicId = $"Employee_{Guid.NewGuid()}",

                    Transformation = new Transformation().Width(500).Height(500).Crop("fill").Gravity("face")
                };

                var uploadResult = await cloudinary.UploadAsync(uploadParams);

                if (uploadResult.Error != null)
                {
                    return BadRequest(new { message = $"อัปโหลดรูปภาพไม่สำเร็จ: {uploadResult.Error.Message}" });
                }

                newEmployee.Image_Profile = uploadResult.SecureUrl.ToString();
            }
            _context.Employee.Add(newEmployee);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "สมัครสมาชิกพนักงานสำเร็จ", Employee = newEmployee });
        }
        [HttpPost("register-member")]
        [Consumes("application/json")]
        public async Task<IActionResult> registerMember([FromBody] registerMemberDtos member)
        {
            if (await _context.Members.AnyAsync(m => m.Email == member.Email))
            {
                return BadRequest(new { Message = "อีเมลนี้ถูกใช้งานแล้ว" });
            }
            if (await _context.Members.AnyAsync(m => m.Phone == member.Phone))
            {
                return BadRequest(new { Message = "เบอร์โทรศัพท์นี้ถูกใช้งานแล้ว" });
            }
            string passwordHash = BCrypt.Net.BCrypt.HashPassword(member.Password);
            var newMember = new Member
            {
                Fullname = member.Fullname,
                Email = member.Email,
                Phone = member.Phone,
                Password = passwordHash,
                Birthday = member.Birthday
            };

            _context.Members.Add(newMember);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "สมัครสมาชิกสำเร็จ", Member = newMember });
        }
        [HttpPut("HashPassword")]
        public async Task<IActionResult> HashPassword(int id)
        {
            var employee = await _context.Employee.FindAsync(id);
            if (employee == null)
            {
                return NotFound(new { Message = "ไม่พบข้อมูลพนักงาน" });
            }

            if (!employee.Password.StartsWith("$2a$"))
            {
                employee.Password = BCrypt.Net.BCrypt.HashPassword(employee.Password);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "รหัสผ่านถูกเข้ารหัสเรียบร้อยแล้ว" });
            }
            else
            {
                return BadRequest(new { Message = "รหัสผ่านนี้ถูกเข้ารหัสแล้ว" });
            }
        }
        
        

        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOtp(string email)
        {
            var isMember = await _context.Members.AnyAsync(u => u.Email == email);
            var isEmployee = await _context.Employee.AnyAsync(u => u.Email == email);

            if (!isMember && !isEmployee)
            {
                return BadRequest(new { message = "ไม่พบอีเมลนี้ในระบบ กรุณาตรวจสอบอีกครั้ง" });
            }
            var otp = new Random().Next(100000, 999999).ToString();


            _cache.Set(email, otp, TimeSpan.FromMinutes(5));

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Buffet Restuarant", "66011212144@msu.ac.th"));
            message.To.Add(new MailboxAddress("", email));
            message.Subject = "รหัส OTP เพื่อเปลี่ยนรหัสผ่าน";
            message.Body = new TextPart("plain") { Text = $"รหัส OTP ของคุณคือ: {otp} (มีอายุ 5 นาที)" };

            using (var client = new SmtpClient())
            {
                try
                {

                    await client.ConnectAsync("smtp.gmail.com", 587, MailKit.Security.SecureSocketOptions.StartTls);
                    await client.AuthenticateAsync("66011212144@msu.ac.th", "jgywcixvqrgnhtqq");
                    await client.SendAsync(message);
                    await client.DisconnectAsync(true);
                    return Ok(new { message = "ส่งรหัส OTP สำเร็จ กรุณาตรวจสอบอีเมลของคุณ" });
                }
                catch (Exception ex)
                {
                    return StatusCode(500, "ส่งเมลไม่สำเร็จ: " + ex.Message);
                }
            }
        }

        [HttpPut("edit-profile-employee")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> EditProfileEmployee([FromForm] editEmployeeProfileDtos employeeDto, [FromServices] Cloudinary cloudinary)
        {
            var employee = await _context.Employee.FirstOrDefaultAsync(e => e.Emp_id == employeeDto.Emp_id);

            if (employee == null)
            {
                return NotFound(new { Message = "ไม่พบข้อมูลพนักงาน" });
            }

            // ตรวจสอบความซ้ำของเบอร์โทรศัพท์ (ถ้ามีการแก้ไขเบอร์)
            if (!string.IsNullOrEmpty(employeeDto.Phone) && employeeDto.Phone != employee.Phone)
            {
                if (await _context.Employee.AnyAsync(e => e.Phone == employeeDto.Phone && e.Emp_id != employeeDto.Emp_id))
                {
                    return BadRequest(new { Message = "เบอร์โทรศัพท์นี้ถูกใช้งานแล้ว" });
                }
                employee.Phone = employeeDto.Phone;
            }

            // ตรวจสอบความซ้ำของอีเมล (ถ้ามีการแก้ไขอีเมล)
            if (!string.IsNullOrEmpty(employeeDto.Email) && employeeDto.Email != employee.Email)
            {
                if (await _context.Employee.AnyAsync(e => e.Email == employeeDto.Email && e.Emp_id != employeeDto.Emp_id))
                {
                    return BadRequest(new { Message = "อีเมลนี้ถูกใช้งานแล้ว" });
                }
                employee.Email = employeeDto.Email;
            }

            // อัปเดตข้อมูลทั่วไป
            if (!string.IsNullOrEmpty(employeeDto.Fullname))
            {
                employee.Fullname = employeeDto.Fullname;
            }

            if (!string.IsNullOrEmpty(employeeDto.Address))
            {
                employee.Address = employeeDto.Address;
            }

            // เปลี่ยนรหัสผ่านแบบ Hash ด้วย BCrypt (ถ้ามีการระบุรหัสผ่านใหม่)
            if (!string.IsNullOrEmpty(employeeDto.Password))
            {
                employee.Password = BCrypt.Net.BCrypt.HashPassword(employeeDto.Password);
            }

            // จัดการอัปโหลดรูปภาพใหม่ไปยัง Cloudinary (ถ้ามี)
            if (employeeDto.Image_Profile != null && employeeDto.Image_Profile.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };
                var extension = Path.GetExtension(employeeDto.Image_Profile.FileName).ToLower();
                if (!allowedExtensions.Contains(extension))
                {
                    return BadRequest(new { Message = "กรุณาอัปโหลดไฟล์รูปภาพ (.jpg, .png) เท่านั้น" });
                }

                using var stream = employeeDto.Image_Profile.OpenReadStream();
                var uploadParams = new ImageUploadParams()
                {
                    File = new FileDescription(employeeDto.Image_Profile.FileName, stream),
                    Folder = "Employee_profiles",
                    PublicId = $"Employee_{Guid.NewGuid()}",
                    Transformation = new Transformation().Width(500).Height(500).Crop("fill").Gravity("face")
                };

                var uploadResult = await cloudinary.UploadAsync(uploadParams);

                if (uploadResult.Error != null)
                {
                    return BadRequest(new { Message = $"อัปโหลดรูปภาพไม่สำเร็จ: {uploadResult.Error.Message}" });
                }

                employee.Image_Profile = uploadResult.SecureUrl.ToString();
            }

            _context.Employee.Update(employee);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "แก้ไขข้อมูลโปรไฟล์พนักงานสำเร็จ", Employee = employee });
        }

        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpReq model)
        {
            if (_cache.TryGetValue(model.Email, out string savedOtp))
            {
                if (savedOtp == model.OtpCode)
                {
                    _cache.Remove(model.Email);
                    _cache.Set($"Verified_{model.Email}", true, TimeSpan.FromMinutes(10));
                    return Ok(new { message = "OTP ถูกต้อง กรุณาตั้งรหัสผ่านใหม่" });
                }
            }

            return BadRequest(new { message = "รหัส OTP ไม่ถูกต้องหรือหมดอายุ กรุณาลองใหม่อีกครั้ง" });
        }
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordReq model)
        {
            if (!_cache.TryGetValue($"Verified_{model.Email}", out bool isVerified) || !isVerified)
            {
                return BadRequest(new { message = "คำขอไม่ถูกต้อง กรุณายืนยัน OTP อีกครั้ง" });
            }
            var Members = await _context.Members.FirstOrDefaultAsync(u => u.Email == model.Email);

            if (Members != null)
            {
                Members.Password = BCrypt.Net.BCrypt.HashPassword(model.NewPassword);
            }
            else
            {
                var employee = await _context.Members.FirstOrDefaultAsync(e => e.Email == model.Email);
                if (employee != null)
                {
                    employee.Password = BCrypt.Net.BCrypt.HashPassword(model.NewPassword);
                }
                else
                {
                    return NotFound(new { message = "ไม่พบอีเมลนี้ในระบบ" });
                }
            }

            await _context.SaveChangesAsync();
            _cache.Remove($"Verified_{model.Email}");

            return Ok(new { message = "เปลี่ยนรหัสผ่านสำเร็จแล้ว!" });
        }
    }
}