using Nulysa.Communications;
using Nulysa.Localization;

namespace Nulysa.Components;

public static class MailKindLabel
{
    public static string Text(MailKind kind) => UiText.T(kind switch
    {
        MailKind.Verification => "Application verification",
        MailKind.Welcome => "Welcome and password setup",
        MailKind.Rejection => "Application rejection",
        MailKind.DeletionWarning => "Deletion warning",
        MailKind.PasswordReset => "Password reset",
        MailKind.PasswordChanged => "Password changed",
        MailKind.EmailChange => "Email change",
        MailKind.Restoration => "Account restoration",
        MailKind.MembershipGrace => "Membership grace notice",
        MailKind.MembershipInactive => "Membership inactivity notice",
        MailKind.BankAlert => "Bank synchronization alert",
        _ => "Invoice"
    });
}
