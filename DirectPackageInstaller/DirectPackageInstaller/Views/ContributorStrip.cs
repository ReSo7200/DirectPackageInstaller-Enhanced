using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DirectPackageInstaller.Services;

namespace DirectPackageInstaller.Views
{
    /// <summary>Someone to credit: a GitHub login (for the avatar and link) and optional links.</summary>
    public sealed record Contributor(string Name, string? GitHub = null, string? LinkedIn = null, string? Role = null);

    /// <summary>
    /// A titled card with a row of contributor cards (avatar, name, GitHub/LinkedIn buttons),
    /// like InstaEclipse's home screen. When the cards don't fit, the row loops by itself
    /// (the cards are listed twice and the offset wraps at one copy) and stops while the
    /// pointer or a finger is on it.
    /// </summary>
    public sealed class ContributorStrip : Border
    {
        const double Speed = 40;   // px per second: a slow conveyor belt

        static readonly uint[] AvatarColors =
        {
            0xFF5E5CE6, 0xFFFF375F, 0xFFFF9F0A, 0xFF30D158, 0xFF64D2FF,
            0xFFBF5AF2, 0xFFFF9500, 0xFF32D74B, 0xFF0A84FF, 0xFFFFD60A
        };

        static readonly Geometry GitHubIcon = Geometry.Parse("M12,2A10,10 0 0,0 2,12C2,16.42 4.87,20.17 8.84,21.5C9.34,21.58 9.5,21.27 9.5,21C9.5,20.77 9.5,20.14 9.5,19.31C6.73,19.91 6.14,17.97 6.14,17.97C5.68,16.81 5.03,16.5 5.03,16.5C4.12,15.88 5.1,15.9 5.1,15.9C6.1,15.97 6.63,16.93 6.63,16.93C7.5,18.45 8.97,18 9.54,17.76C9.63,17.11 9.89,16.67 10.17,16.42C7.95,16.17 5.62,15.31 5.62,11.5C5.62,10.39 6,9.5 6.65,8.79C6.55,8.54 6.2,7.5 6.75,6.15C6.75,6.15 7.59,5.88 9.5,7.17C10.29,6.95 11.15,6.84 12,6.84C12.85,6.84 13.71,6.95 14.5,7.17C16.41,5.88 17.25,6.15 17.25,6.15C17.8,7.5 17.45,8.54 17.35,8.79C18,9.5 18.38,10.39 18.38,11.5C18.38,15.32 16.04,16.16 13.81,16.41C14.17,16.72 14.5,17.33 14.5,18.26C14.5,19.6 14.5,20.68 14.5,21C14.5,21.27 14.66,21.59 15.17,21.5C19.14,20.16 22,16.42 22,12A10,10 0 0,0 12,2Z");
        static readonly Geometry LinkedInIcon = Geometry.Parse("M19,0h-14c-2.761,0 -5,2.239 -5,5v14c0,2.761 2.239,5 5,5h14c2.762,0 5,-2.239 5,-5v-14c0,-2.761 -2.238,-5 -5,-5zM8,19h-3v-11h3v11zM6.5,6.732c-0.966,0 -1.75,-0.79 -1.75,-1.764s0.784,-1.764 1.75,-1.764 1.75,0.79 1.75,1.764 -0.783,1.764 -1.75,1.764zM20,19h-3v-5.604c0,-3.368 -4,-3.113 -4,0v5.604h-3v-11h3v1.765c1.396,-2.586 7,-2.777 7,2.476v6.759z");

        readonly IReadOnlyList<Contributor> People;
        readonly ScrollViewer Scroller;
        readonly StackPanel Row;
        readonly DispatcherTimer Timer;
        bool Looping, Held;
        DateTime LastTick;

        public ContributorStrip(string Title, IReadOnlyList<Contributor> People)
        {
            this.People = People;
            // the card look (the Border.card style only matches plain Borders)
            Background = (IBrush)Application.Current!.FindResource("Deck")!;
            BorderBrush = (IBrush)Application.Current!.FindResource("Line")!;
            BorderThickness = new Thickness(1);
            CornerRadius = Application.Current!.FindResource("Radius") is CornerRadius Radius ? Radius : new CornerRadius(12);
            ClipToBounds = true;
            Padding = new Thickness(16, 16, 16, 12);

            Row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var Person in People)
                Row.Children.Add(Card(Person));

            Scroller = new ScrollViewer
            {
                Content = Row,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            // the user takes over while touching or hovering; the loop resumes from there
            Scroller.PointerEntered += (_, _) => Held = true;
            Scroller.PointerExited += (_, _) => { Held = false; LastTick = DateTime.UtcNow; };
            Scroller.PointerPressed += (_, _) => Held = true;
            Scroller.PointerReleased += (_, _) => { Held = App.IsSingleView ? false : Held; LastTick = DateTime.UtcNow; };

            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = Title, FontSize = 15, FontWeight = FontWeight.SemiBold },
                    Scroller
                }
            };

            Timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
            Scroller.PropertyChanged += (_, e) =>
            {
                if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
                    UpdateLooping();
            };
            AttachedToVisualTree += (_, _) => UpdateLooping();
            DetachedFromVisualTree += (_, _) => Timer.Stop();
        }

        /// <summary>Loop only when one copy of the cards is wider than the space for them.</summary>
        void UpdateLooping()
        {
            if (Scroller.Viewport.Width <= 0)
                return;
            double OneCopy = Looping ? Scroller.Extent.Width / 2 : Scroller.Extent.Width;
            bool Needed = OneCopy > Scroller.Viewport.Width + 1;

            if (Needed && !Looping)
            {
                Looping = true;
                foreach (var Person in People)
                    Row.Children.Add(Card(Person));   // the second copy makes the wrap invisible
            }
            if (Looping && !Timer.IsEnabled && IsVisible)
            {
                LastTick = DateTime.UtcNow;
                Timer.Start();
            }
        }

        void Tick()
        {
            var Now = DateTime.UtcNow;
            double Seconds = (Now - LastTick).TotalSeconds;
            LastTick = Now;
            if (!Looping || Held || !IsEffectivelyVisible)
                return;

            double Half = Scroller.Extent.Width / 2;
            if (Half <= 0)
                return;
            double X = (Scroller.Offset.X + Speed * Math.Min(Seconds, 0.1)) % Half;
            Scroller.Offset = new Vector(X, 0);
        }

        Control Card(Contributor Person)
        {
            var Name = Person.Name.Trim();
            var Initial = Name.FirstOrDefault(char.IsLetterOrDigit);
            var Color = Avalonia.Media.Color.FromUInt32(AvatarColors[(int)((uint)StableHash(Name) % AvatarColors.Length)]);

            // monogram under the photo: it shows when there's no GitHub photo or it fails to load
            var Photo = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
            var Avatar = new Border
            {
                Width = 50, Height = 50, CornerRadius = new CornerRadius(25), ClipToBounds = true,
                Background = new SolidColorBrush(Color),
                Child = new Panel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Initial == default ? "?" : char.ToUpperInvariant(Initial).ToString(),
                            FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                        },
                        Photo
                    }
                }
            };
            if (Person.GitHub != null)
                _ = LoadPhotoAsync(Person.GitHub, Photo);

            var Ring = new Panel
            {
                Width = 56, Height = 56, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Ellipse { Stroke = (IBrush)Application.Current!.FindResource("Signal")!, StrokeThickness = 2 },
                    new Border { Child = Avatar, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                }
            };

            var Links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            if (Person.GitHub != null)
                Links.Children.Add(LinkButton(GitHubIcon, $"https://github.com/{Person.GitHub}", "GitHub"));
            if (Person.LinkedIn != null)
                Links.Children.Add(LinkButton(LinkedInIcon, Person.LinkedIn, "LinkedIn"));

            var Card = new Border
            {
                Width = 132,
                CornerRadius = new CornerRadius(22),
                Background = (IBrush)Application.Current!.FindResource("Raised")!,
                Padding = new Thickness(12, 16, 12, 12),
                Child = new StackPanel
                {
                    Children =
                    {
                        Ring,
                        new TextBlock
                        {
                            Text = Name, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 10)
                        },
                        Links
                    }
                }
            };
            if (Person.Role != null)
                ToolTip.SetTip(Card, Person.Role);
            return Card;
        }

        Button LinkButton(Geometry Icon, string Url, string Label)
        {
            var Button = new Button
            {
                Width = 34, Height = 34, Padding = new Thickness(8), CornerRadius = new CornerRadius(17),
                Background = (IBrush)Application.Current!.FindResource("Hover")!,
                Content = new PathIcon { Data = Icon, Width = 16, Height = 16 }
            };
            ToolTip.SetTip(Button, $"{Label}: {Url}");
            Button.Click += async (_, _) =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(this) is { } Top)
                        await Top.Launcher.LaunchUriAsync(new Uri(Url));
                }
                catch { }
            };
            return Button;
        }

        // ----- GitHub photos: github.com/<login>.png, cached for a week

        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        static readonly ConcurrentDictionary<string, Task<Bitmap?>> Photos = new(StringComparer.OrdinalIgnoreCase);

        static async Task LoadPhotoAsync(string Login, Image Target)
        {
            var Bitmap = await Photos.GetOrAdd(Login, FetchPhotoAsync);
            if (Bitmap != null)
            {
                Target.Source = Bitmap;
                Target.IsVisible = true;
            }
        }

        static async Task<Bitmap?> FetchPhotoAsync(string Login)
        {
            try
            {
                var Folder = System.IO.Path.Combine(LibraryService.DataDir, "avatars");
                var File = System.IO.Path.Combine(Folder, SafeNames.Of(Login) + ".png");
                if (!System.IO.File.Exists(File) || DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(File) > TimeSpan.FromDays(7))
                {
                    var Data = await Http.GetByteArrayAsync($"https://github.com/{Uri.EscapeDataString(Login)}.png?size=112");
                    Directory.CreateDirectory(Folder);
                    await System.IO.File.WriteAllBytesAsync(File, Data);
                }
                return new Bitmap(File);
            }
            catch
            {
                return null;   // offline: the initial stays
            }
        }

        /// <summary>string.GetHashCode changes per run; the colour shouldn't.</summary>
        static int StableHash(string Text)
        {
            unchecked
            {
                int Hash = 17;
                foreach (var C in Text)
                    Hash = Hash * 31 + C;
                return Hash & 0x7FFFFFFF;
            }
        }
    }
}
