using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;

namespace DawloomAttendance.Services
{
    /// <summary>Sends report emails (with a PDF attachment) over SMTP using <see cref="EmailSettings"/>.</summary>
    public static class EmailService
    {
        /// <summary>
        /// Sends one email with an optional attachment. Throws on failure (bad credentials,
        /// unreachable server, …) so the caller can record the error against the recipient.
        /// </summary>
        public static void Send(EmailSettings s, string toEmail, string subject, string body, string attachmentPath = null, string fromName = null)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (!s.IsConfigured) throw new InvalidOperationException("Email is not configured (set the SMTP server and From address in Settings).");
            if (string.IsNullOrWhiteSpace(toEmail)) throw new ArgumentException("Recipient email is empty.", nameof(toEmail));

            using (var msg = new MailMessage())
            {
                msg.From = new MailAddress(s.FromAddress, string.IsNullOrWhiteSpace(fromName) ? s.FromAddress : fromName);
                msg.To.Add(toEmail.Trim());
                msg.Subject = subject ?? "";
                msg.Body = body ?? "";

                Attachment attachment = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(attachmentPath))
                    {
                        attachment = new Attachment(attachmentPath);
                        msg.Attachments.Add(attachment);
                    }

                    using (var client = new SmtpClient(s.Host, s.Port))
                    {
                        client.EnableSsl = s.UseSsl;
                        client.DeliveryMethod = SmtpDeliveryMethod.Network;
                        if (!string.IsNullOrWhiteSpace(s.Username))
                            client.Credentials = new NetworkCredential(s.Username, s.Password);
                        client.Send(msg);
                    }
                }
                finally
                {
                    attachment?.Dispose();
                }
            }
        }

        /// <summary>Builds an email subject from a template, substituting {Name} and {Period}.</summary>
        public static string FormatSubject(string template, string name, string period)
        {
            return (template ?? "")
                .Replace("{Name}", name ?? "")
                .Replace("{Period}", period ?? "");
        }

        /// <summary>Basic sanity check that a string looks like an email address.</summary>
        public static bool LooksLikeEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            email = email.Trim();
            int at = email.IndexOf('@');
            return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at;
        }
    }

    /// <summary>Per-recipient outcome of a batch send, for summarising to the user.</summary>
    public class EmailSendOutcome
    {
        public string Enroll { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public bool Sent { get; set; }
        public string Error { get; set; }   // null when Sent, or "no email" / SMTP error text

        public static EmailSendOutcome Ok(string enroll, string name, string email) =>
            new EmailSendOutcome { Enroll = enroll, Name = name, Email = email, Sent = true };
        public static EmailSendOutcome Fail(string enroll, string name, string email, string error) =>
            new EmailSendOutcome { Enroll = enroll, Name = name, Email = email, Sent = false, Error = error };
    }
}
