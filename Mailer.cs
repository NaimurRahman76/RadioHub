using System.Net.Mail;
using System.Net;

namespace RadioStation
{
	public class Mailer
	{
		public async Task<bool> Send(string toEmail,int pin) {

			var mail = "naimur76rahman@outlook.com";
			var pass = "janina5566";
			var title = "Signup Varification Code";
			var body = "Use this " + pin.ToString() + " number to varify";
			var client = new SmtpClient("smtp-mail.outlook.com", 587)
			{
				EnableSsl = true,
				Credentials = new NetworkCredential(mail, pass)
			};
			try
			{
				await client.SendMailAsync(
					new MailMessage(from:mail,
								to:toEmail,
								title,
								body

					));
			}
			catch
			{
				return false;
			}
			
			return true;
		}
	}
}
