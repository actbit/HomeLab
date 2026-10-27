using System.Net;
using System.Net.Mail;

namespace HomeLab.Server.Authentication;

public sealed class PasswordResetMailer(IConfiguration configuration)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Email:SmtpHost"])
        && !string.IsNullOrWhiteSpace(configuration["Email:From"])
        && Uri.TryCreate(configuration["PublicBaseUrl"], UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    public async Task SendAsync(string email, string token)
    {
        var baseUri = new Uri(configuration["PublicBaseUrl"]!.TrimEnd('/') + "/");
        var relative = $"reset-password#email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        var resetUri = new Uri(baseUri, relative);
        using var mail = new MailMessage(configuration["Email:From"]!, email)
        {
            Subject = "HomeLab パスワード再設定",
            Body = $"パスワードを再設定するには、次のリンクを開いてください。\n\n{resetUri}\n\n心当たりがない場合は、このメールを破棄してください。"
        };
        using var smtp = new SmtpClient(configuration["Email:SmtpHost"]!, configuration.GetValue("Email:SmtpPort", 587))
        {
            EnableSsl = true,
            Timeout = 15000
        };
        var username = configuration["Email:Username"];
        if (!string.IsNullOrWhiteSpace(username))
            smtp.Credentials = new NetworkCredential(username, configuration["Email:Password"]);
        await smtp.SendMailAsync(mail);
    }
}
