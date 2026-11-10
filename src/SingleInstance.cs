using System.Security.AccessControl;
using System.Security.Principal;

namespace RgbManager;

/// <summary>
/// Une seule instance : relancer l'appli (raccourci du bureau…) ouvre le menu de celle qui tourne déjà.
/// L'instance qui tourne est en admin et celle qu'on relance non : l'événement autorise donc
/// explicitement l'utilisateur courant à le déclencher.
/// </summary>
static class SingleInstance
{
    const string EventName = @"Local\RgbManager.ShowMenu";

    /// <summary>Signale l'instance déjà lancée. Renvoie true s'il y en a une.</summary>
    public static bool SignalExisting()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(EventName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize);
            ev.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // elle existe mais refuse le signal : on ne lance pas de doublon
        }
    }

    /// <summary>Crée l'événement que l'instance principale surveille.</summary>
    public static EventWaitHandle CreateShowEvent()
    {
        var security = new EventWaitHandleSecurity();
        var user = WindowsIdentity.GetCurrent().User;
        security.AddAccessRule(new EventWaitHandleAccessRule(user,
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out _, security);
    }
}
