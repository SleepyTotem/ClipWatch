using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClipWatch;

internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClipWatch;component/resources/Theme.xaml")
        });
        var folder = Path.Combine(Path.GetTempPath(), "ClipWatch-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var controller = new Controller(new Config());
            void Check(bool ok, string message)
            {
                if (!ok) throw new Exception(message);
                Console.WriteLine("PASS " + message);
            }

            Check(ClipOps.Sanitize("CON") == "_CON", "Reserved Windows filename");
            Check(ClipOps.Sanitize("clip... ") == "clip", "Trailing dots and whitespace");
            Check(ClipOps.Sanitize("<>:") == "clip", "Empty sanitized filename");
            var source = Path.Combine(folder, "original.mp4");
            File.WriteAllText(source, "original clip bytes");
            var clip = new Clip { Path = source, DisplayName = "original" };
            Check(ClipOps.Rename(clip, "renamed") == null && File.Exists(clip.Path)
                && clip.DisplayName == "renamed", "Rename updates the model and file");
            File.WriteAllText(Path.Combine(folder, "occupied.mp4"), "existing clip");
            Check(ClipOps.Rename(clip, "occupied") != null && File.ReadAllText(clip.Path) == "original clip bytes",
                "Rename collision preserves both files");
            Check(ClipOps.UniquePath(folder, "occupied", ".mp4").EndsWith("occupied (2).mp4"), "Unique output filename");
            Check(SeekBar.FormatSpan(3661.5) == "1:01:01.5", "Long-clip elapsed time");
            var editor = new EditorView(null!);
            editor.Load(new Clip { Path = Path.Combine(folder, "missing.mp4"), DisplayName = "Replay preview", Duration = TimeSpan.FromSeconds(65) });
            Check(((TextBlock)editor.FindName("PlayerNotice")).Text.Contains("moved or deleted"), "Missing-file explanation");
            Check(!((Button)editor.FindName("SaveButton")).IsEnabled, "Missing-file save disabled");
            var play = (Button)editor.FindName("PlayButton");
            Check(!play.Focusable && !play.IsTabStop && play.FocusVisualStyle == null, "Play control cannot receive focus");

            var destination = (RadioButton)editor.FindName("DestReplace");
            Check(!destination.Focusable && !destination.IsTabStop,
                "Export options cannot receive Space");
            Check(((TextBlock)editor.FindName("TimeLabel")).Text == "0:00.0 / 1:05.0", "Elapsed and total time displayed");
            var replay = new PlaybackWindow(controller);
            replay.Load(Path.Combine(folder, "missing.mp4"));
            Check(((TextBlock)replay.FindName("PlayerNotice")).Text.Contains("moved or deleted"), "Replay missing-file explanation");
            replay.Close();
            Check(replay.WindowStyle == WindowStyle.None && replay.ResizeMode == ResizeMode.NoResize,
                "Replay has no window chrome");
            var timeline = (SeekBar)editor.FindName("Timeline");
            Check(timeline.IsTrimEnabled && !((SeekBar)replay.FindName("Seek")).IsTrimEnabled,
                "Editor and replay share the filmstrip with editor-only trim handles");
            timeline.InPoint = 999;
            Check(timeline.InPoint < timeline.OutPoint, "Trim handles cannot cross");
            timeline.Duration = 0;
            Check(timeline.Duration > 0 && double.IsFinite(timeline.SelectionLength), "Zero-duration timeline stays valid");
            var disposable = Path.Combine(folder, "temporary.mkv");
            File.WriteAllText(disposable, "test replay");
            var discard = new PlaybackWindow(controller);
            discard.Load(disposable);
            discard.Close();
            discard.DiscardCompletion.GetAwaiter().GetResult();
            Check(!File.Exists(disposable), "Closing an unkept replay deletes the temporary file");
            var savedFolder = Path.Combine(folder, "saved");
            Directory.CreateDirectory(savedFolder);
            controller.Library.SetFolder(savedFolder);
            File.WriteAllText(disposable, "test replay");
            var keep = new PlaybackWindow(controller);
            keep.Load(disposable);
            ((Button)keep.FindName("KeepButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            keep.Close();
            keep.DiscardCompletion.GetAwaiter().GetResult();
            Check(keep.KeptPath != null && File.ReadAllText(keep.KeptPath) == "test replay" && !File.Exists(disposable),
                "A kept replay survives dismissal");

            ExportRequest Request(ExportMode mode = ExportMode.Fast, int? height = null,
                                  params AudioMix[] audio) => new()
            {
                Source = @"C:\in.mp4",
                Destination = @"C:\out.mp4",
                Start = TimeSpan.FromSeconds(2),
                End = TimeSpan.FromSeconds(7),
                Mode = mode,
                TargetHeight = height,
                Audio = audio
            };

            var untouched = Exporter.BuildArguments(Request(audio: new AudioMix(0, 1.0, false)));
            Check(untouched.Contains("-c:v copy") && untouched.Contains("-c:a copy")
                  && !untouched.Contains("filter_complex"),
                "An unchanged mix copies both streams");

            var quieter = Exporter.BuildArguments(Request(audio: new AudioMix(0, 0.5, false)));
            Check(quieter.Contains("-c:v copy") && quieter.Contains("volume=0.5")
                  && quieter.Contains("[aout]") && quieter.Contains("-c:a aac"),
                "Changing one track's volume re-encodes audio but still copies video");

            var mixed = Exporter.BuildArguments(Request(audio: new[]
            {
                new AudioMix(0, 1.0, false),
                new AudioMix(1, 0.4, false),
                new AudioMix(2, 1.0, true)
            }));
            Check(mixed.Contains("amix=inputs=2:normalize=0") && !mixed.Contains("[0:a:2]"),
                "A muted track is excluded and the rest are mixed without normalisation");

            var soloed = Exporter.BuildArguments(Request(audio: new[]
            {
                new AudioMix(0, 0.8, false),
                new AudioMix(1, 1.0, true)
            }));
            Check(soloed.Contains("[aout]") && !soloed.Contains("amix"),
                "One surviving track needs no mixer");

            var silent = Exporter.BuildArguments(Request(audio: new AudioMix(0, 1.0, true)));
            Check(silent.Contains("-an"), "Muting every track exports silent video");

            var precise = Exporter.BuildArguments(Request(ExportMode.Precise));
            Check(precise.Contains("libx264") && precise.Contains("-crf"),
                "Precise mode re-encodes the video");

            var scaled = Exporter.BuildArguments(Request(ExportMode.Fast, 720));
            Check(scaled.Contains("scale=-2:720") && scaled.Contains("libx264"),
                "Scaling forces a video re-encode even in Fast mode");

            Check(Exporter.ExtensionFor(ExportContainer.KeepOriginal, ".mkv") == ".mkv"
                  && Exporter.ExtensionFor(ExportContainer.Mp4, ".mkv") == ".mp4",
                "Container choice picks the output extension");

            var probe = new MediaInfo(1920, 1080, 60, 8_000_000, 7_800_000,
                new[] { new AudioTrack(0, null, null, 2) });
            var copyEstimate = Exporter.EstimateBytes(
                Request(audio: new AudioMix(0, 1.0, false)), probe);
            Check(copyEstimate > 4_000_000 && copyEstimate < 7_000_000,
                "Copy-mode size estimate follows the source bitrate");

            Check(Exporter.EstimateBytes(Request(ExportMode.Fast, 720, new AudioMix(0, 1.0, false)), probe)
                  < copyEstimate, "Downscaling lowers the estimate");

            var layered = new Config
            {
                EnableAudioLayers = true,
                AudioLayers =
                {
                    new AudioLayer { Process = "Discord", Label = "Discord", Track = 2 },
                    new AudioLayer { Process = "Spotify", Label = "Spotify", Track = 4 }
                }
            };

            Check(AudioLayerSetup.TrackMask(layered) == 0b1011,
                "Track mask covers the main mix plus each configured layer");
            Check(AudioLayerSetup.NextFreeTrack(layered) == 3,
                "The next layer takes the lowest free track");
            Check(new AudioTrack(3, null, null, 2).DisplayName(layered) == "Spotify",
                "A track is named after the layer recorded onto it");
            Check(new AudioTrack(1, "Game", null, 2).DisplayName(layered) == "Game",
                "A title written into the file wins over the configured label");

            if (args.Contains("--ui") || args.Contains("--overlay"))
            {
                var window = new MainWindow(controller, new Config())
                {
                    Title = "ClipWatch UI review", AllowClose = true
                };
                window.OpenClipByPath(Path.Combine(folder, "Replay preview.mp4"));
                var uiEditor = (EditorView)((ContentControl)window.FindName("Host")).Content;
                uiEditor.Load(new Clip { Path = Path.Combine(folder, "Replay preview.mp4"), DisplayName = "Replay preview", Duration = TimeSpan.FromSeconds(65) });
                var uiTimeline = (SeekBar)uiEditor.FindName("Timeline");
                var frames = Enumerable.Range(0, 14).Select(i => (ImageSource)new DrawingImage(
                    new GeometryDrawing(new LinearGradientBrush(Color.FromRgb((byte)(30+i*7), 80, 110),
                        Color.FromRgb(18, 25, 40), 45), null, new RectangleGeometry(new Rect(0,0,100,56))))).ToArray();
                uiTimeline.SetFilmstrip(frames);
                PlaybackWindow? overlay = null;
                if (args.Contains("--overlay"))
                {
                    File.WriteAllText(disposable, "disposable overlay test");
                    overlay = new PlaybackWindow(controller);
                    overlay.Load(disposable);
                    ((SeekBar)overlay.FindName("Seek")).Duration = 65;
                    ((SeekBar)overlay.FindName("Seek")).SetFilmstrip(frames);
                    overlay.Closed += (_, _) => app.Shutdown();
                    window.Loaded += (_, _) => overlay.Show();
                }
                window.Closed += (_, _) => app.Shutdown();
                app.Run(window);
                if (overlay != null)
                {
                    overlay.DiscardCompletion.GetAwaiter().GetResult();
                    Check(!File.Exists(disposable), "Dismissed overlay deletes the temporary replay");
                }
            }
            editor.Stop();
            Console.WriteLine("All regression checks passed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(folder, true); }
    }
}
