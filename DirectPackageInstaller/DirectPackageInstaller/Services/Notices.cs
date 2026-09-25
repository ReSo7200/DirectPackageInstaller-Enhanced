using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace DirectPackageInstaller.Services
{
    /// <summary>
    /// Short pop-ups in the corner of the window (queue items finishing, moves ending),
    /// so nobody has to keep watching the Queue. The window or phone shell attaches.
    /// </summary>
    public static class Notices
    {
        static WindowNotificationManager? Manager;

        public static void Attach(TopLevel Host, bool AtTop = false)
        {
            Manager = new WindowNotificationManager(Host)
            {
                Position = AtTop ? NotificationPosition.TopCenter : NotificationPosition.BottomRight,
                MaxItems = 3
            };
        }

        public static void Post(string Title, string Message, bool Error = false) => Dispatcher.UIThread.Post(() =>
            Manager?.Show(new Notification(Title, Message, Error ? NotificationType.Error : NotificationType.Success,
                TimeSpan.FromSeconds(Error ? 12 : 6))));
    }
}
