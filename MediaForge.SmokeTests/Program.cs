using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaForge;
using MediaForge.Core;
using MediaForge.Models;
using MediaForge.Services;
using MediaForge.ViewModels;
using MediaForge.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var application = new App();
            application.InitializeComponent();
            var window = new MainWindow
            {
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            Flush(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var home = (HomeViewModel)viewModel.CurrentPage;
            var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "ui"));
            Directory.CreateDirectory(output);
            Capture(window, Path.Combine(output, "01-home.png"));

            var expected = new Dictionary<string, Type>
            {
                ["download"] = typeof(DownloadViewModel), ["cut"] = typeof(CutViewModel), ["editor"] = typeof(EditorViewModel),
                ["convert"] = typeof(ConvertViewModel), ["speed"] = typeof(SpeedViewModel), ["link"] = typeof(LinkViewModel),
                ["sorter"] = typeof(SorterViewModel), ["gallery"] = typeof(GalleryViewModel)
            };
            var index = 2;
            foreach (var (key, type) in expected)
            {
                home.NavigateCommand.Execute(key);
                Flush(window);
                if (viewModel.CurrentPage.GetType() != type) throw new InvalidOperationException($"Navigation zu {key} lieferte {viewModel.CurrentPage.GetType().Name}.");
                Capture(window, Path.Combine(output, $"{index++:00}-{key}.png"));
                home.NavigateCommand.Execute("home");
                Flush(window);
            }
            var settings = new SettingsWindow
            {
                Owner = window,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            settings.Show();
            Flush(settings);
            Capture(settings, Path.Combine(output, "07-settings.png"));
            settings.Close();

            var markerWindow = new MarkerWindow(new MediaMarker
            {
                Kind = MarkerKind.Range, StartMs = 763_000, EndMs = 797_000,
                Title = "Tor Müller", Note = "Sehr guter Spielzug über links",
                Category = "Highlight", Tags = SidecarService.NormalizeTags("#tor #müller #highlight")
            }, isNew: false, durationMs: 5_423_000)
            {
                Owner = window,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            markerWindow.Show();
            Flush(markerWindow);
            Capture(markerWindow, Path.Combine(output, "08-marker.png"));
            markerWindow.Close();

            RunSorterChecks(output, window, home);
            RunGalleryChecks(output);
            RunSettingsChecks();
            RunSidecarChecks();
            RunThumbnailEmbeddingChecks();
            RunServiceChecks(output);
            window.Close();
            application.Shutdown();
            Console.WriteLine("UI_SMOKE_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Capture(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>
    /// Prueft den File Sorter an einem Wegwerf-Ordner: Einlesen, Ablegen, Rueckgaengig, Fortschritt —
    /// und haelt die Ansicht mit echten Dateien als Screenshot fest.
    /// </summary>
    private static void RunSorterChecks(string outputRoot, Window window, HomeViewModel home)
    {
        var root = Path.Combine(Path.GetTempPath(), "mediaforge_sorter_" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "Eingang");
        var clips = Path.Combine(root, "Clips");
        var fotos = Path.Combine(root, "Fotos");
        var erledigt = Path.Combine(source, "Erledigt");
        var backup = File.Exists(SettingsService.FilePath) ? SettingsService.Current.Clone() : null;

        Directory.CreateDirectory(Path.Combine(source, "Urlaub"));
        Directory.CreateDirectory(clips);
        Directory.CreateDirectory(fotos);
        Directory.CreateDirectory(erledigt);
        WriteSamplePng(Path.Combine(source, "bild.png"), 40, 120, 220);
        WriteSamplePng(Path.Combine(source, "Urlaub", "strand.png"), 220, 140, 60);
        File.WriteAllText(Path.Combine(source, "notiz.txt"), "Nur Text, keine Vorschau.");
        File.WriteAllText(Path.Combine(source, "clip.mp4"), "kein echtes Video");
        File.WriteAllText(Path.Combine(erledigt, "schon-sortiert.mp4"), "liegt bereits im Ziel");

        var targets = new List<SorterTarget>
        {
            new() { Name = "Clips", Folder = clips },
            new() { Name = "Fotos", Folder = fotos },
            new() { Name = "Erledigt", Folder = erledigt, Key = "x" }
        };

        try
        {
            var scanned = SorterService.Scan(source, includeSubfolders: true, targets);
            if (scanned.Count != 4)
                throw new InvalidOperationException($"Sorter-Einlesen lieferte {scanned.Count} statt 4 Dateien.");
            if (scanned.Any(item => item.Name == "schon-sortiert.mp4"))
                throw new InvalidOperationException("Zielordner im Startordner wurde nicht ausgeschlossen.");
            if (scanned.Count(item => item.Kind == SorterItemKind.Video) != 1 ||
                scanned.Count(item => item.Kind == SorterItemKind.Image) != 2 ||
                scanned.Count(item => item.Kind == SorterItemKind.File) != 1)
                throw new InvalidOperationException("Sorter-Dateitypen wurden falsch erkannt.");

            var strand = scanned.Single(item => item.Name == "strand.png");
            if (strand.Subfolder != "Urlaub")
                throw new InvalidOperationException("Unterordner der Datei wurde nicht erkannt.");

            var moved = SorterService.PlaceAsync(strand, targets[1], copy: false, preserveSubfolders: true).GetAwaiter().GetResult();
            var expected = Path.Combine(fotos, "Urlaub", "strand.png");
            if (moved != expected || !File.Exists(expected) || File.Exists(strand.FullPath))
                throw new InvalidOperationException("Verschieben in den Zielbereich fehlgeschlagen.");

            var entry = new SorterProgressEntry
            {
                RelativePath = strand.RelativePath, SourcePath = strand.FullPath,
                Target = "Fotos", Destination = moved, Copied = false
            };
            var progress = new SorterProgress { Source = source, Entries = [entry] };
            if (!SorterService.SaveProgress(progress)) throw new InvalidOperationException("Fortschritt konnte nicht gespeichert werden.");
            var reloaded = SorterService.LoadProgress(source);
            if (reloaded.Entries.Count != 1 || reloaded.Entries[0].Destination != moved)
                throw new InvalidOperationException("Fortschritts-Rundlauf fehlgeschlagen.");

            SorterService.UndoAsync(entry).GetAwaiter().GetResult();
            if (!File.Exists(strand.FullPath) || File.Exists(moved))
                throw new InvalidOperationException("Rueckgaengig hat die Datei nicht zurueckgeholt.");

            // Gleicher Name im Ziel: die Ablage bekommt automatisch eine Nummer.
            var bild = scanned.Single(item => item.Name == "bild.png");
            File.WriteAllText(Path.Combine(fotos, "bild.png"), "belegt");
            var second = SorterService.PlaceAsync(bild, targets[1], copy: true, preserveSubfolders: false).GetAwaiter().GetResult();
            if (Path.GetFileName(second) != "bild_1.png" || !File.Exists(bild.FullPath))
                throw new InvalidOperationException("Namenskonflikt oder Kopiermodus fehlgeschlagen.");
            File.Delete(second);
            File.Delete(Path.Combine(fotos, "bild.png"));

            var backupRoot = Path.Combine(source, "Backup");
            Directory.CreateDirectory(Path.Combine(backupRoot, "Archiv", "Fotos"));
            var placed = SorterService.PlaceAsync(strand, targets[1], copy: false, preserveSubfolders: true).GetAwaiter().GetResult();
            var backedUp = SorterService.BackupAsync(placed, fotos, backupRoot).GetAwaiter().GetResult();
            if (backedUp != Path.Combine(backupRoot, "Archiv", "Fotos", "Urlaub", "strand.png") ||
                !File.ReadAllBytes(placed).SequenceEqual(File.ReadAllBytes(backedUp)))
                throw new InvalidOperationException("Backup hat Struktur oder Inhalt nicht erhalten.");
            var duplicateBackup = SorterService.BackupAsync(placed, fotos, backupRoot).GetAwaiter().GetResult();
            if (duplicateBackup == backedUp || !File.Exists(backedUp))
                throw new InvalidOperationException("Backup überschreibt vorhandene Dateien.");
            if (SorterService.Scan(source, true, targets, backupRoot).Any(item => item.FullPath.StartsWith(backupRoot)))
                throw new InvalidOperationException("Backup wird erneut zum Sortieren eingelesen.");
            SorterService.UndoAsync(new SorterProgressEntry { Destination = placed, SourcePath = strand.FullPath }).GetAwaiter().GetResult();
            if (!File.Exists(backedUp)) throw new InvalidOperationException("Rückgängig entfernt das Backup.");
            if (SorterService.ResolveBackupFolder(backupRoot, clips) != Path.Combine(backupRoot, "Clips"))
                throw new InvalidOperationException("Fehlendes Backup-Ziel wurde falsch zugeordnet.");
            Directory.CreateDirectory(Path.Combine(backupRoot, "Weiteres", "Fotos"));
            var ambiguous = false;
            try { SorterService.ResolveBackupFolder(backupRoot, fotos); }
            catch (IOException) { ambiguous = true; }
            if (!ambiguous) throw new InvalidOperationException("Mehrdeutige Backup-Ziele wurden akzeptiert.");
            var nestedTarget = Path.Combine(root, "Sammlung", "Fotos");
            var exactBackup = Path.Combine(backupRoot, "Sammlung", "Fotos");
            Directory.CreateDirectory(exactBackup);
            if (SorterService.ResolveBackupFolder(backupRoot, nestedTarget) != exactBackup)
                throw new InvalidOperationException("Die längste Pfadübereinstimmung wurde nicht bevorzugt.");
            var overlapRejected = false;
            try { SorterService.ResolveBackupFolder(backupRoot, Path.Combine(backupRoot, "Fotos")); }
            catch (IOException) { overlapRejected = true; }
            if (!overlapRejected) throw new InvalidOperationException("Backup und Sortierziel dürfen sich nicht überlappen.");
            Directory.Delete(backupRoot, true);

            // Ansicht mit echten Dateien zeigen.
            var settings = (backup ?? AppSettings.CreateDefault()).Clone();
            settings.SorterSourceDirectory = source;
            settings.SorterBackupEnabled = false;
            settings.SorterTargets = targets;
            settings.SorterIncludeSubfolders = true;
            settings.SorterAutoPlay = false;
            SettingsService.Save(settings);
            home.ApplySettings();

            home.NavigateCommand.Execute("sorter");
            Flush(window);
            Capture(window, Path.Combine(outputRoot, "09-sorter-bild.png"));

            var sorter = (SorterViewModel)((MainWindowViewModel)window.DataContext).CurrentPage;
            if (sorter.Targets.Count != 3 || sorter.Targets[0].Key != "1" || sorter.Targets[2].Key != "x")
                throw new InvalidOperationException("Tastenbelegung der Zielbereiche stimmt nicht.");
            if (!sorter.HasCurrent || !sorter.IsImage)
                throw new InvalidOperationException("Erste Datei wird nicht als Bild angezeigt.");

            sorter.NextCommand.Execute(null);
            Flush(window);
            if (!sorter.IsVideo) throw new InvalidOperationException("Zweite Datei wird nicht als Video gefuehrt.");
            Capture(window, Path.Combine(outputRoot, "10-sorter-video.png"));

            sorter.NextCommand.Execute(null);
            Flush(window);
            if (!sorter.IsPlainFile) throw new InvalidOperationException("Dritte Datei wird nicht als Datei gefuehrt.");
            Capture(window, Path.Combine(outputRoot, "11-sorter-datei.png"));

            var sorterSettings = new SettingsWindow
            {
                Owner = window, Left = -20000, Top = -20000,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual
            };
            sorterSettings.Show();
            Flush(sorterSettings);
            Capture(sorterSettings, Path.Combine(outputRoot, "12-einstellungen-sorter.png"));
            var scroller = FindChild<ScrollViewer>(sorterSettings)
                ?? throw new InvalidOperationException("Einstellungen ohne Bildlauf.");
            scroller.ScrollToEnd();
            Flush(sorterSettings);
            Capture(sorterSettings, Path.Combine(outputRoot, "13-einstellungen-zielbereiche.png"));
            sorterSettings.Close();

            home.NavigateCommand.Execute("home");
            Flush(window);
        }
        finally
        {
            if (backup is not null) SettingsService.Save(backup);
            else try { File.Delete(SettingsService.FilePath); } catch { }
            home.ApplySettings();
            try { File.Delete(SorterService.ProgressFile(source)); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Prueft die Galerie an einem Wegwerf-Ordner: Einlesen, Vorschaubilder, Bibliotheksdatei —
    /// und haelt die Zeitleiste mit echten Kacheln als Screenshot fest.
    /// </summary>
    private static void RunGalleryChecks(string outputRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "mediaforge_gallery_" + Guid.NewGuid().ToString("N"));
        var urlaub = Path.Combine(root, "Urlaub");

        // Eigene Bibliothek im Wegwerf-Ordner: die echte Sammlung des Anwenders bleibt unberuehrt,
        // auch wenn im Hintergrund noch Vorschaubilder nachlaufen und speichern.
        // Bewusst neben und nicht im Startordner - sonst zaehlt der Scan die Vorschaubilder mit.
        var bibliothek = root + "-bibliothek";
        GalleryStore.DirectoryOverride = bibliothek;

        Directory.CreateDirectory(urlaub);
        WriteSamplePng(Path.Combine(root, "strand.png"), 40, 120, 220);
        WriteSamplePng(Path.Combine(root, "sonne.png"), 230, 170, 60);
        WriteSamplePng(Path.Combine(urlaub, "hafen.png"), 60, 190, 150);
        File.WriteAllText(Path.Combine(root, "clip.mp4"), "kein echtes Video");
        File.WriteAllText(Path.Combine(root, "notiz.txt"), "Weder Bild noch Video.");

        try
        {
            var scanned = GalleryService.Scan([root]);
            if (scanned.Count != 4)
                throw new InvalidOperationException($"Galerie-Einlesen lieferte {scanned.Count} statt 4 Eintraege.");
            if (scanned.Any(item => item.Name == "notiz.txt"))
                throw new InvalidOperationException("Textdatei wurde nicht aussortiert.");
            if (scanned.Count(item => item.Kind == GalleryKind.Image) != 3 ||
                scanned.Count(item => item.Kind == GalleryKind.Video) != 1)
                throw new InvalidOperationException("Galerie-Dateitypen wurden falsch erkannt.");

            var hafen = scanned.First(item => item.Name == "hafen.png");
            if (hafen.Subfolder != "Urlaub")
                throw new InvalidOperationException($"Unterordner wurde als \"{hafen.Subfolder}\" statt \"Urlaub\" erkannt.");

            var thumbnail = GalleryService.ThumbnailAsync(hafen, new FfmpegService()).GetAwaiter().GetResult();
            if (thumbnail is null) throw new InvalidOperationException("Fuer ein Bild entstand kein Vorschaubild.");
            if (!File.Exists(GalleryService.ThumbnailPath(hafen)))
                throw new InvalidOperationException("Das Vorschaubild wurde nicht zwischengespeichert.");

            var tags = GalleryTags.Parse("#Urlaub, strand  #URLAUB");
            if (tags.Count != 2 || tags[0] != "strand" || tags[1] != "urlaub")
                throw new InvalidOperationException("Tags wurden nicht vereinheitlicht: " + string.Join('|', tags));

            // Bibliotheksdatei: Ordner, Favorit, Tag und Album muessen den Rundlauf ueberstehen.
            var album = new GalleryAlbum { Name = "Sommer" };
            album.Keys.Add(hafen.Key);
            album.RefreshCount();
            var document = new GalleryDocument
            {
                Roots = [root],
                Entries =
                [
                    new GalleryEntryRecord
                    {
                        Path = hafen.Key, Name = hafen.Name, Size = hafen.Size, Favorite = true, Tags = tags
                    }
                ],
                Albums = GalleryStore.FromAlbums([album])
            };
            if (!GalleryStore.Save(document)) throw new InvalidOperationException("Bibliothek konnte nicht gespeichert werden.");

            var reloaded = GalleryStore.Load();
            if (reloaded.Roots.Count != 1 || !string.Equals(reloaded.Roots[0], root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Der Ordner fehlt nach dem Neuladen.");
            var entry = GalleryStore.EntryMap(reloaded).GetValueOrDefault(hafen.Key)
                ?? throw new InvalidOperationException("Der Eintrag fehlt nach dem Neuladen.");
            if (!entry.Favorite || entry.Tags.Count != 2)
                throw new InvalidOperationException("Favorit oder Tags gingen beim Neuladen verloren.");
            var albums = GalleryStore.ToAlbums(reloaded);
            if (albums.Count != 1 || albums[0].Count != 1 || albums[0].Name != "Sommer")
                throw new InvalidOperationException("Das Album kam nicht unveraendert zurueck.");

            // Die Ansicht mit echten Kacheln rendern.
            var model = new GalleryViewModel(new FfmpegService(), () => { });
            var host = new Window
            {
                Content = new GalleryView { DataContext = model },
                Width = 1280,
                Height = 760,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            host.Show();
            Pump(host, () => model.Days.Count > 0);
            if (model.Days.Count == 0) throw new InvalidOperationException("Die Zeitleiste blieb leer.");
            var shown = model.Days.Sum(day => day.Items.Count);
            if (shown != 4) throw new InvalidOperationException($"Die Zeitleiste zeigt {shown} statt 4 Eintraege.");
            Pump(host, () => model.Days.SelectMany(day => day.Items).Any(item => item.Thumbnail is not null));
            Capture(host, Path.Combine(outputRoot, "14-galerie.png"));

            model.ShowFavoritesCommand.Execute(null);
            Flush(host);
            var favorites = model.Days.Sum(day => day.Items.Count);
            if (favorites != 1) throw new InvalidOperationException($"Die Favoriten zeigen {favorites} statt 1 Eintrag.");

            // Die drei Sichten oben in der Seitenleiste.
            model.ShowVideosCommand.Execute(null);
            Flush(host);
            var nurVideos = model.Days.SelectMany(day => day.Items).ToList();
            if (nurVideos.Count != 1 || nurVideos[0].Kind != GalleryKind.Video)
                throw new InvalidOperationException($"Die Videosicht zeigt {nurVideos.Count} statt 1 Video.");
            if (model.NavKey != "videos")
                throw new InvalidOperationException("Die Videosicht wird in der Seitenleiste nicht hervorgehoben.");

            model.ShowPhotosCommand.Execute(null);
            Flush(host);
            var nurFotos = model.Days.Sum(day => day.Items.Count);
            if (nurFotos != 3 || model.NavKey != "photos")
                throw new InvalidOperationException($"Die Fotosicht zeigt {nurFotos} statt 3 Fotos.");

            model.ShowAllCommand.Execute(null);
            Flush(host);
            if (model.Days.Sum(day => day.Items.Count) != 4 || model.NavKey != "all")
                throw new InvalidOperationException("Die komplette Galerie zeigt nicht alle vier Eintraege.");

            // Sortierung: nach Name entstehen Buchstabengruppen, nach Dateiart je eine Gruppe pro Endung.
            model.Sort = GalleryViewModel.SortName;
            Flush(host);
            var namen = model.Days.SelectMany(day => day.Items).Select(item => item.Name).ToList();
            if (!namen.SequenceEqual(namen.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException("Namenssortierung stimmt nicht: " + string.Join('|', namen));
            if (model.Days.Count < 2)
                throw new InvalidOperationException("Nach Name entstanden keine Buchstabengruppen.");
            Capture(host, Path.Combine(outputRoot, "17-galerie-sortierung.png"));

            model.Sort = GalleryViewModel.SortSize;
            Flush(host);
            var groessen = model.Days.SelectMany(day => day.Items).Select(item => item.Size).ToList();
            if (!groessen.SequenceEqual(groessen.OrderByDescending(size => size)))
                throw new InvalidOperationException("Groessensortierung stimmt nicht.");

            model.Sort = GalleryViewModel.SortKind;
            Flush(host);
            if (model.Days.Count != 2)
                throw new InvalidOperationException($"Nach Dateiart entstanden {model.Days.Count} statt 2 Gruppen.");

            model.Sort = GalleryViewModel.SortNewest;
            Flush(host);

            model.SearchText = "#urlaub";
            Flush(host);
            var found = model.Days.Sum(day => day.Items.Count);
            if (found != 1) throw new InvalidOperationException($"Die Tagsuche fand {found} statt 1 Eintrag.");
            Capture(host, Path.Combine(outputRoot, "15-galerie-suche.png"));

            // Einzelansicht an einem Bild: Vorschau, Angaben und Tagliste muessen stehen.
            model.SearchText = string.Empty;
            Flush(host);
            var hafenBild = model.Days.SelectMany(day => day.Items).First(item => item.Name == "hafen.png");
            model.OpenViewerCommand.Execute(hafenBild);
            Pump(host, () => model.ViewerImage is not null);
            if (!model.IsViewerOpen || model.ViewerImage is null)
                throw new InvalidOperationException("Die Einzelansicht zeigte kein Bild.");
            Capture(host, Path.Combine(outputRoot, "16-galerie-einzelansicht.png"));
            // Alben in der Einzelansicht: das Haekchen zeigt und aendert die Zugehoerigkeit.
            var sommer = model.Albums.Single(album => album.Name == "Sommer");
            model.OpenViewerCommand.Execute(hafenBild);
            Flush(host);
            var mitgliedschaft = model.CurrentAlbums.Single(eintrag => eintrag.Album == sommer);
            if (!mitgliedschaft.IsMember)
                throw new InvalidOperationException("Die Einzelansicht zeigt nicht, dass der Eintrag schon im Album liegt.");
            if (model.CurrentAlbumsText != "In 1 Album")
                throw new InvalidOperationException($"Das Menue meldet „{model.CurrentAlbumsText}“ statt „In 1 Album“.");

            mitgliedschaft.IsMember = false;
            Flush(host);
            if (sommer.Keys.Contains(hafenBild.Key) || sommer.Count != 0)
                throw new InvalidOperationException("Das Haekchen hat den Eintrag nicht aus dem Album genommen.");
            if (model.CurrentAlbumsText != "In keinem Album")
                throw new InvalidOperationException($"Das Menue meldet „{model.CurrentAlbumsText}“ statt „In keinem Album“.");
            mitgliedschaft.IsMember = true;
            Flush(host);
            if (!sommer.Keys.Contains(hafenBild.Key) || sommer.Count != 1)
                throw new InvalidOperationException("Das Haekchen hat den Eintrag nicht ins Album gelegt.");

            // Das aufgeklappte Menue muss sich zeichnen lassen - Popups haengen in einem eigenen Fenster.
            var ansicht = (GalleryView)host.Content;
            var knopf = (System.Windows.Controls.Primitives.ToggleButton)ansicht.FindName("AlbumMenu")
                ?? throw new InvalidOperationException("Der Menueknopf wurde nicht gefunden.");
            var menue = (System.Windows.Controls.Primitives.Popup)ansicht.FindName("AlbumPopup")
                ?? throw new InvalidOperationException("Das Albummenue wurde nicht gefunden.");
            knopf.IsChecked = true;
            Flush(host);
            if (!menue.IsOpen) throw new InvalidOperationException("Das Albummenue oeffnete nicht.");
            CaptureElement((FrameworkElement)menue.Child, Path.Combine(outputRoot, "22-galerie-albummenue.png"));
            knopf.IsChecked = false;
            Flush(host);

            // Umbenennen ueber den Befehl aus dem Kontextmenue, samt Abbrechen mit Escape.
            model.RenameAlbumCommand.Execute(sommer);
            if (!sommer.IsRenaming || sommer.EditName != "Sommer")
                throw new InvalidOperationException("Das Umbenennen des Albums startete nicht.");
            sommer.EditName = "Quatsch";
            model.CancelRename(sommer);
            if (sommer.IsRenaming || sommer.Name != "Sommer")
                throw new InvalidOperationException("Das Abbrechen hat den Albumnamen trotzdem geaendert.");

            model.RenameAlbumCommand.Execute(sommer);
            sommer.EditName = "Sommer 2026";
            model.CommitRename(sommer);
            Flush(host);
            if (sommer.IsRenaming || sommer.Name != "Sommer 2026")
                throw new InvalidOperationException($"Das Album heisst „{sommer.Name}“ statt „Sommer 2026“.");
            model.RenameAlbumCommand.Execute(sommer);
            sommer.EditName = "Sommer";
            model.CommitRename(sommer);
            Flush(host);

            var frisch = model.Albums.Single(album => album.Name == "Sommer");
            if (model.CurrentAlbums.Any(eintrag => eintrag.Album != frisch && eintrag.IsMember))
                throw new InvalidOperationException("Ein fremdes Album meldet faelschlich Zugehoerigkeit.");

            // Tags in der Einzelansicht: Menue mit Haekchen statt Marken und Freitext.
            if (model.CurrentTagsText != "2 Tags")
                throw new InvalidOperationException($"Das Tagmenue meldet „{model.CurrentTagsText}“ statt „2 Tags“.");
            var strandTag = model.CurrentTags.Single(eintrag => eintrag.Name == "strand");
            strandTag.IsMember = false;
            Flush(host);
            if (hafenBild.Tags.Count != 1 || model.CurrentTagsText != "1 Tag")
                throw new InvalidOperationException("Das Haekchen hat den Tag nicht entfernt.");
            strandTag.IsMember = true;
            Flush(host);
            if (hafenBild.Tags.Count != 2 || model.CurrentTagsText != "2 Tags")
                throw new InvalidOperationException("Das Haekchen hat den Tag nicht zurueckgegeben.");

            // Tag anlegen, ohne ihn zu vergeben - er muss danach im Menue zur Wahl stehen.
            model.NewTagName = "reise";
            model.CreateTagCommand.Execute(null);
            Flush(host);
            var vorrat = model.TagList.SingleOrDefault(eintrag => eintrag.Name == "reise")
                ?? throw new InvalidOperationException("Der angelegte Tag fehlt in der Seitenleiste.");
            if (vorrat.Count != 0)
                throw new InvalidOperationException("Ein frisch angelegter Tag haengt schon an Eintraegen.");
            if (model.CurrentTags.All(eintrag => eintrag.Name != "reise"))
                throw new InvalidOperationException("Der angelegte Tag steht nicht im Menue des Eintrags.");
            if (model.CurrentTagsText != "2 Tags")
                throw new InvalidOperationException("Der angelegte Tag gilt faelschlich als vergeben.");

            // Das Feld mit + haengt einen neuen Tag direkt an den Eintrag.
            model.ItemTagName = "hafen";
            model.AddItemTagCommand.Execute(null);
            Flush(host);
            if (!hafenBild.Tags.Contains("hafen") || model.CurrentTagsText != "3 Tags")
                throw new InvalidOperationException("Das Feld mit + hat den Tag nicht vergeben.");
            if (model.ItemTagName.Length != 0)
                throw new InvalidOperationException("Das Eingabefeld wurde nach dem Anlegen nicht geleert.");

            var tagKnopf = (System.Windows.Controls.Primitives.ToggleButton)ansicht.FindName("TagMenu")
                ?? throw new InvalidOperationException("Der Tagmenueknopf wurde nicht gefunden.");
            var tagMenue = (System.Windows.Controls.Primitives.Popup)ansicht.FindName("TagPopup")
                ?? throw new InvalidOperationException("Das Tagmenue wurde nicht gefunden.");
            tagKnopf.IsChecked = true;
            Flush(host);
            if (!tagMenue.IsOpen) throw new InvalidOperationException("Das Tagmenue oeffnete nicht.");
            CaptureElement((FrameworkElement)tagMenue.Child, Path.Combine(outputRoot, "23-galerie-tagmenue.png"));
            tagKnopf.IsChecked = false;
            Flush(host);

            // Umbenennen zieht den Tag an allen Eintraegen mit.
            var hafenTag = model.TagList.Single(eintrag => eintrag.Name == "hafen");
            model.RenameTagCommand.Execute(hafenTag);
            if (!hafenTag.IsRenaming)
                throw new InvalidOperationException("Das Umbenennen des Tags startete nicht.");
            hafenTag.EditName = "hafenstadt";
            model.CommitRename(hafenTag);
            Flush(host);
            if (!hafenBild.Tags.Contains("hafenstadt") || hafenBild.Tags.Contains("hafen"))
                throw new InvalidOperationException("Der umbenannte Tag wanderte nicht am Eintrag mit.");
            if (model.TagList.All(eintrag => eintrag.Name != "hafenstadt"))
                throw new InvalidOperationException("Der umbenannte Tag fehlt in der Seitenleiste.");

            // Entfernen nimmt den Tag ueberall weg - die Datei bleibt liegen.
            model.DeleteTagCommand.Execute(model.TagList.Single(eintrag => eintrag.Name == "hafenstadt"));
            Flush(host);
            if (hafenBild.Tags.Count != 2 || hafenBild.Tags.Contains("hafenstadt"))
                throw new InvalidOperationException("Der entfernte Tag haengt noch am Eintrag.");
            if (model.TagList.Any(eintrag => eintrag.Name == "hafenstadt"))
                throw new InvalidOperationException("Der entfernte Tag steht noch in der Seitenleiste.");
            if (!File.Exists(hafenBild.FullPath))
                throw new InvalidOperationException("Das Entfernen eines Tags hat die Datei geloescht.");

            model.IsViewerOpen = false;
            Flush(host);

            // Das Kontextmenue haengt wie die Popups in einem eigenen Fenster - also eigens pruefen.
            var besitzer = FindMenuOwner(ansicht)
                ?? throw new InvalidOperationException("In der Seitenleiste hat nichts ein Kontextmenue.");
            var kontext = besitzer.ContextMenu!;
            kontext.PlacementTarget = besitzer;
            kontext.IsOpen = true;
            Flush(host);
            var ersterEintrag = kontext.Items.OfType<MenuItem>().First();
            if (ersterEintrag.Command is null)
                throw new InvalidOperationException("Der Kontextmenue-Eintrag fand seinen Befehl nicht.");
            if (ersterEintrag.CommandParameter is null)
                throw new InvalidOperationException("Der Kontextmenue-Eintrag kennt seinen Gegenstand nicht.");
            CaptureElement(kontext, Path.Combine(outputRoot, "24-galerie-kontextmenue.png"));
            kontext.IsOpen = false;
            Flush(host);

            // Mehrfachauswahl: standardmaessig ist kein Album angehakt.
            if (model.Albums.Any(album => album.IsChecked))
                throw new InvalidOperationException("Es ist von vornherein ein Album angehakt.");

            model.NewAlbumName = "Zweitalbum";
            model.CreateAlbumCommand.Execute(null);
            var zweitalbum = model.Albums.Single(album => album.Name == "Zweitalbum");
            if (zweitalbum.IsChecked)
                throw new InvalidOperationException("Ein neu angelegtes Album ist sofort angehakt.");

            model.ToggleSelectionCommand.Execute(hafenBild);
            Flush(host);
            if (sommer.SelectionHint != "1/1")
                throw new InvalidOperationException($"Das Album meldet „{sommer.SelectionHint}“ statt „1/1“ fuer die Auswahl.");
            if (zweitalbum.SelectionHint.Length != 0)
                throw new InvalidOperationException("Ein leeres Album meldet Zugehoerigkeit.");

            if (model.AddSelectionToAlbumCommand.CanExecute(null))
                throw new InvalidOperationException("„Ins Album“ ist bedienbar, obwohl kein Album angehakt ist.");
            sommer.IsChecked = true;
            zweitalbum.IsChecked = true;
            if (!model.AddSelectionToAlbumCommand.CanExecute(null))
                throw new InvalidOperationException("„Ins Album“ bleibt gesperrt, obwohl Alben angehakt sind.");
            Flush(host);
            Capture(host, Path.Combine(outputRoot, "21-galerie-albumauswahl.png"));
            model.AddSelectionToAlbumCommand.Execute(null);
            Flush(host);
            if (!zweitalbum.Keys.Contains(hafenBild.Key) || zweitalbum.Count != 1)
                throw new InvalidOperationException("Die Mehrfachauswahl hat nicht in alle angehakten Alben gelegt.");

            model.RemoveSelectionFromAlbumCommand.Execute(null);
            Flush(host);
            if (sommer.Count != 0 || zweitalbum.Count != 0)
                throw new InvalidOperationException("Das Entfernen wirkte nicht auf alle angehakten Alben.");

            sommer.IsChecked = true;
            model.AddSelectionToAlbumCommand.Execute(null);
            Flush(host);
            model.ClearAlbumChoiceCommand.Execute(null);
            model.DeleteAlbumCommand.Execute(zweitalbum);
            model.ClearSelectionCommand.Execute(null);
            Flush(host);
            if (sommer.Count != 1 || model.Albums.Count != 1)
                throw new InvalidOperationException("Der Ausgangszustand der Alben wurde nicht wiederhergestellt.");

            // Unterordner stehen in der Seitenleiste unter ihrem Ordner und lassen sich oeffnen.
            var wurzel = model.Roots.Single();
            if (wurzel.Subfolders.Count != 1 || wurzel.Subfolders[0].Name != "Urlaub" || wurzel.Subfolders[0].Count != 1)
                throw new InvalidOperationException("Der Unterordner „Urlaub“ fehlt in der Seitenleiste.");
            model.OpenFolderCommand.Execute(wurzel.Subfolders[0]);
            Flush(host);
            if (model.Days.Sum(day => day.Items.Count) != 1 || model.Headline != "Urlaub")
                throw new InvalidOperationException("Die Unterordnersicht zeigt nicht genau ihren einen Eintrag.");
            Capture(host, Path.Combine(outputRoot, "18-galerie-unterordner.png"));

            // Im Ordner gehoeren Tags und Alben nur zu ihm.
            if (!model.HasScope || model.ScopeText != "Urlaub")
                throw new InvalidOperationException("Der gewaehlte Ordner wird nicht als Bezug angezeigt.");
            if (model.VisibleAlbums.Count != 1 || model.VisibleAlbums[0].ScopedCount != 1)
                throw new InvalidOperationException("Die Albumliste bezieht sich nicht auf den Ordner.");
            if (model.TagList.Count != 2 || model.TagList.Any(eintrag => eintrag.Count != 1))
                throw new InvalidOperationException("Die Tagliste bezieht sich nicht auf den Ordner.");

            // Ein Tag aus dem Ordner heraus bleibt im Ordner.
            model.OpenTagCommand.Execute(model.TagList.First(eintrag => eintrag.Name == "urlaub"));
            Flush(host);
            if (!model.HasScope || model.Days.Sum(day => day.Items.Count) != 1)
                throw new InvalidOperationException("Die Tagsicht verliess den gewaehlten Ordner.");

            model.ShowAllCommand.Execute(null);
            Flush(host);
            if (model.HasScope)
                throw new InvalidOperationException("Die oberste Sicht hat den Ordnerbezug nicht aufgehoben.");
            if (model.VisibleAlbums.Count != 1 || model.VisibleAlbums[0].ScopedCount != 1)
                throw new InvalidOperationException("Ohne Ordner zeigt die Albumliste nicht den vollen Stand.");

            // Der Platzbedarf steht in der Fusszeile.
            if (!model.StorageText.Contains("1 Ordner", StringComparison.Ordinal))
                throw new InvalidOperationException($"Die Platzangabe lautet „{model.StorageText}“.");
            var erwartet = model.Days.SelectMany(day => day.Items).Sum(item => item.Size);
            if (erwartet == 0) throw new InvalidOperationException("Die Eintraege melden keine Groesse.");

            // In einen anderen Unterordner verschoben: Favorit, Tags und Album muessen mitwandern.
            var reisen = Path.Combine(root, "Reisen");
            Directory.CreateDirectory(reisen);
            var neuerPfad = Path.Combine(reisen, "hafen.png");
            File.Move(Path.Combine(urlaub, "hafen.png"), neuerPfad);
            host.Dispatcher.Invoke(() => model.RescanCommand.Execute(null));
            Pump(host, () => model.Days.SelectMany(day => day.Items)
                .Any(item => string.Equals(item.FullPath, neuerPfad, StringComparison.OrdinalIgnoreCase)));

            var verschoben = model.Days.SelectMany(day => day.Items)
                .FirstOrDefault(item => string.Equals(item.FullPath, neuerPfad, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Die verschobene Datei wurde nicht eingelesen.");
            if (!verschoben.IsFavorite || verschoben.Tags.Count != 2)
                throw new InvalidOperationException("Favorit oder Tags gingen beim Verschieben verloren.");
            if (!model.Albums[0].Keys.Contains(verschoben.Key))
                throw new InvalidOperationException("Die Albumzugehoerigkeit ging beim Verschieben verloren.");
            if (model.Roots.Single().Subfolders.Any(folder => folder.Name == "Urlaub"))
                throw new InvalidOperationException("Der leere Unterordner steht noch in der Seitenleiste.");

            // Tiefere Ebenen: der Baum nimmt sie auf, zaehlt sie beim Elternordner mit und startet zugeklappt.
            var tief = Path.Combine(reisen, "2024", "Hafen");
            Directory.CreateDirectory(tief);
            WriteSamplePng(Path.Combine(tief, "bucht.png"), 90, 90, 200);
            host.Dispatcher.Invoke(() => model.RescanCommand.Execute(null));
            Pump(host, () => model.Roots.Count == 1 &&
                             model.Roots[0].Subfolders.Any(folder => folder.Name == "Reisen" && folder.HasChildren));

            var reisenOrdner = model.Roots[0].Subfolders.Single(folder => folder.Name == "Reisen");
            if (reisenOrdner.Count != 2)
                throw new InvalidOperationException($"Der Ordner zaehlt {reisenOrdner.Count} statt 2 Eintraege (Unterordner mitgerechnet).");
            var jahr = reisenOrdner.Children.Single();
            if (jahr.Label != "2024" || jahr.Count != 1)
                throw new InvalidOperationException("Die zweite Ebene des Ordnerbaums stimmt nicht.");
            if (jahr.IsExpanded)
                throw new InvalidOperationException("Tiefere Ebenen sollen zugeklappt starten.");
            var hafenOrdner = jahr.Children.Single();
            if (hafenOrdner.Label != "Hafen" || hafenOrdner.Name != Path.Combine("Reisen", "2024", "Hafen") || hafenOrdner.HasChildren)
                throw new InvalidOperationException("Die dritte Ebene des Ordnerbaums stimmt nicht.");

            reisenOrdner.IsExpanded = true;
            jahr.IsExpanded = true;
            Flush(host);
            model.OpenFolderCommand.Execute(hafenOrdner);
            Flush(host);
            if (model.Days.Sum(day => day.Items.Count) != 1 || model.Headline != hafenOrdner.Name)
                throw new InvalidOperationException("Die tiefste Ordnersicht zeigt nicht ihren einen Eintrag.");
            Capture(host, Path.Combine(outputRoot, "19-galerie-ordnerbaum.png"));

            // Leerer Zustand: Favoriten bieten nichts an, ein leeres Album will gefuellt werden.
            model.ShowFavoritesCommand.Execute(null);
            Flush(host);
            if (model.CanAddRootHere || model.IsAlbumOpen)
                throw new InvalidOperationException("Die Favoriten bieten im leeren Zustand etwas an.");

            // Der Hinweisbereich listet Duplikate und fehlerhafte Dateien. Dafuer erst drei Funde
            // anlegen: eine wortgleiche Kopie, ein beschaedigtes Bild und eine leere Datei.
            model.ShowAllCommand.Execute(null);
            Flush(host);
            var kopie = Path.Combine(reisen, "strand-kopie.png");
            File.Copy(Path.Combine(root, "strand.png"), kopie);
            File.WriteAllText(Path.Combine(root, "kaputt.png"), "das ist kein PNG");
            File.WriteAllBytes(Path.Combine(root, "leer.jpg"), []);
            host.Dispatcher.Invoke(() => model.RescanCommand.Execute(null));
            Pump(host, () => model.Days.SelectMany(day => day.Items).Any(item => item.Name == "leer.jpg"));

            model.ShowNotesCommand.Execute(null);
            Flush(host);
            if (!model.IsNotes || model.NavKey != "notes" || model.Headline != "Hinweise")
                throw new InvalidOperationException("Der Hinweisbereich laesst sich nicht oeffnen.");
            if (model.Days.Count != 0 || model.CanAddRootHere || model.IsAlbumOpen)
                throw new InvalidOperationException("Der Hinweisbereich zeigt die Zeitleiste oder bietet Ordner an.");
            if (model.HasChecked || model.NotesCountText.Length != 0)
                throw new InvalidOperationException("Der Hinweisbereich meldet Funde, ohne geprueft zu haben.");

            host.Dispatcher.Invoke(() => model.CheckCommand.Execute(null));
            Pump(host, () => model.HasChecked, 60000);
            if (!model.HasChecked) throw new InvalidOperationException("Die Pruefung wurde nicht fertig.");

            var gruppe = model.Duplicates.SingleOrDefault(eintrag => eintrag.Items.Any(fund => fund.Name == "strand-kopie.png"))
                ?? throw new InvalidOperationException("Die Kopie wurde nicht als Duplikat erkannt.");
            if (gruppe.Items.Count != 2 || gruppe.Items.Count(fund => fund.IsOriginal) != 1)
                throw new InvalidOperationException($"Die Duplikatgruppe hat {gruppe.Items.Count} Eintraege statt 2.");
            if (model.Duplicates.Count != 1)
                throw new InvalidOperationException($"Es wurden {model.Duplicates.Count} Duplikatgruppen statt einer gefunden.");
            if (model.Broken.All(fund => fund.Name != "kaputt.png"))
                throw new InvalidOperationException("Das beschaedigte Bild fehlt in den Hinweisen.");
            if (model.Broken.All(fund => fund.Name != "leer.jpg"))
                throw new InvalidOperationException("Die leere Datei fehlt in den Hinweisen.");
            if (model.Broken.Any(fund => fund.Name == "strand.png" || fund.Name == "bucht.png"))
                throw new InvalidOperationException("Ein einwandfreies Bild steht faelschlich in den Hinweisen.");
            if (!model.HasDuplicates || !model.HasBroken || model.IsClean || model.NotesCountText.Length == 0)
                throw new InvalidOperationException("Die Zahlen am Hinweisbereich stimmen nicht.");
            Capture(host, Path.Combine(outputRoot, "25-galerie-hinweise.png"));

            model.NewAlbumName = "Leer";
            model.CreateAlbumCommand.Execute(null);
            var leeresAlbum = model.Albums.Single(album => album.Name == "Leer");
            model.OpenAlbumCommand.Execute(leeresAlbum);
            Flush(host);
            if (model.HasItems) throw new InvalidOperationException("Das neue Album ist nicht leer.");
            if (model.CanAddRootHere) throw new InvalidOperationException("Im Album steht noch „Ordner hinzufuegen“.");
            if (!model.IsAlbumOpen) throw new InvalidOperationException("Das Album bietet kein Hinzufuegen an.");
            Capture(host, Path.Combine(outputRoot, "20-galerie-leeres-album.png"));

            model.FillAlbumCommand.Execute(null);
            Flush(host);
            if (model.Section != GallerySection.All || !leeresAlbum.IsChecked)
                throw new InvalidOperationException("„Bilder/Videos hinzufuegen“ fuehrt nicht in die Galerie mit dem Album als Ziel.");
            model.DeleteAlbumCommand.Execute(leeresAlbum);
            Flush(host);

            // Ordner entfernen: die Dateien verschwinden aus der Ansicht, die Ordnung bleibt gespeichert.
            host.Dispatcher.Invoke(() => model.RemoveRootCommand.Execute(model.Roots.Single()));
            Pump(host, () => model.Roots.Count == 0);
            if (model.Days.Count != 0)
                throw new InvalidOperationException("Nach dem Entfernen blieben Eintraege stehen.");
            var nachEntfernen = GalleryStore.Load();
            if (nachEntfernen.Roots.Count != 0)
                throw new InvalidOperationException("Der Ordner steht nach dem Entfernen noch in der Bibliothek.");
            if (nachEntfernen.Entries.Count == 0 || nachEntfernen.Albums.Count != 1 || nachEntfernen.Albums[0].Paths.Count != 1)
                throw new InvalidOperationException("Das Entfernen hat Favoriten, Tags oder Alben mitgeloescht.");
            host.Close();

            // Wieder aufnehmen: eine frische Ansicht muss alles unveraendert vorfinden.
            nachEntfernen.Roots.Add(root);
            GalleryStore.Save(nachEntfernen);
            var zweites = new GalleryViewModel(new FfmpegService(), () => { });
            var wieder = new Window
            {
                Content = new GalleryView { DataContext = zweites },
                Width = 1280,
                Height = 760,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            wieder.Show();
            Pump(wieder, () => zweites.Days.Count > 0);
            var erneut = zweites.Days.SelectMany(day => day.Items).FirstOrDefault(item => item.Name == "hafen.png")
                ?? throw new InvalidOperationException("Nach dem Wiederaufnehmen fehlt die Datei.");
            if (!erneut.IsFavorite || erneut.Tags.Count != 2)
                throw new InvalidOperationException("Favorit oder Tags kamen beim Wiederaufnehmen nicht zurueck.");
            if (zweites.Albums.Count != 1 || !zweites.Albums[0].Keys.Contains(erneut.Key))
                throw new InvalidOperationException("Das Album kam beim Wiederaufnehmen nicht zurueck.");
            if (zweites.Roots.Single().Subfolders.All(folder => folder.Name != "Reisen"))
                throw new InvalidOperationException("Der neue Unterordner fehlt nach dem Wiederaufnehmen.");
            wieder.Close();
        }
        finally
        {
            GalleryStore.DirectoryOverride = null;
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(bibliothek, true); } catch { }
        }
    }

    /// <summary>Pumpt die Oberflaeche, bis die Bedingung zutrifft oder die Zeit ablaeuft.</summary>
    private static void Pump(Window window, Func<bool> until, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !until())
        {
            Flush(window);
            System.Threading.Thread.Sleep(25);
        }
        Flush(window);
    }

    /// <summary>Die erste Schaltflaeche der Seitenleiste, an der ein Kontextmenue haengt.</summary>
    private static Button? FindMenuOwner(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { ContextMenu: not null } button) return button;
            if (FindMenuOwner(child) is { } found) return found;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static void WriteSamplePng(string path, byte r, byte g, byte b)
    {
        const int width = 320;
        const int height = 200;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 3;
            pixels[i] = (byte)(r * (x + 1) / width);
            pixels[i + 1] = (byte)(g * (y + 1) / height);
            pixels[i + 2] = b;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void RunSettingsChecks()
    {
        var probe = Path.Combine(Path.GetTempPath(), "mediaforge_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        var tool = Path.Combine(probe, "ffmpeg.exe");
        File.WriteAllText(tool, "stub");
        try
        {
            if (ToolLocator.Find("ffmpeg.exe", tool) != tool)
                throw new InvalidOperationException("Konfigurierter Werkzeugpfad wird ignoriert.");
            if (ToolLocator.Find("ffmpeg.exe", probe) != tool)
                throw new InvalidOperationException("Konfigurierter Werkzeugordner wird ignoriert.");
            if (ToolLocator.Find("ffmpeg.exe", Path.Combine(probe, "fehlt.exe")) == tool)
                throw new InvalidOperationException("Ungültiger Werkzeugpfad wurde nicht verworfen.");

            var defaults = AppSettings.CreateDefault();
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(defaults))
                ?? throw new InvalidOperationException("Einstellungen konnten nicht gelesen werden.");
            if (restored.CookieBrowser != defaults.CookieBrowser || restored.UrlsFile != defaults.UrlsFile ||
                restored.CustomOutputDirectory != defaults.CustomOutputDirectory)
                throw new InvalidOperationException("Einstellungs-Rundlauf fehlgeschlagen.");
            if (SettingsService.OutputDirectory(useCustom: true).Length == 0 ||
                SettingsService.OutputDirectory(useCustom: false).Length == 0)
                throw new InvalidOperationException("Zielordner-Auflösung fehlgeschlagen.");
        }
        finally { try { Directory.Delete(probe, true); } catch { } }
    }

    /// <summary>Prüft Pfadbildung, Zeitformate und den JSON-Rundlauf der Marker-Sidecar-Datei.</summary>
    private static void RunSidecarChecks()
    {
        var probePath = SidecarService.PathFor(Path.Combine("C:", "videos", "bundesliga_vfb_bayern.mp4"));
        if (Path.GetFileName(probePath) != "bundesliga_vfb_bayern.mediaforge.json")
            throw new InvalidOperationException("Sidecar-Pfad wird falsch gebildet.");
        if (Formatters.TimeFromMs(763_000) != "00:12:43" || Formatters.TimeFromMs(763_500, true) != "00:12:43.500")
            throw new InvalidOperationException("Zeitanzeige in Millisekunden fehlgeschlagen.");
        if (Formatters.ParseTimeToMs("00:12:43") != 763_000)
            throw new InvalidOperationException("Zeiteingabe wird nicht in Millisekunden umgesetzt.");
        var tags = SidecarService.NormalizeTags("#Tor, Müller  highlight #tor");
        if (tags.Count != 3 || tags[0] != "tor" || tags[1] != "müller" || tags[2] != "highlight")
            throw new InvalidOperationException("Tag-Normalisierung fehlgeschlagen.");

        var directory = Path.Combine(Path.GetTempPath(), "mediaforge_sidecar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var media = Path.Combine(directory, "bundesliga_vfb_bayern.mp4");
        File.WriteAllText(media, "stub");
        try
        {
            var sidecar = SidecarService.LoadAsync(media, null).GetAwaiter().GetResult();
            if (sidecar.Existed) throw new InvalidOperationException("Nicht vorhandene Sidecar-Datei wurde als vorhanden gemeldet.");
            sidecar.DurationMs = 5_423_000;
            sidecar.TagsText = "#bundesliga #vfb";
            sidecar.Markers.Add(new MediaMarker
            {
                Id = "marker-001", Kind = MarkerKind.Point, StartMs = 763_000, EndMs = 763_000,
                Title = "Tor Müller", Note = "Sehr guter Spielzug über links",
                Category = "Highlight", Tags = SidecarService.NormalizeTags("#tor #müller #highlight")
            });
            sidecar.Markers.Add(new MediaMarker
            {
                Id = "range-001", Kind = MarkerKind.Range, StartMs = 1_103_000, EndMs = 1_145_000,
                Title = "Interview Trainer", Category = "interview", Tags = SidecarService.NormalizeTags("interview trainer")
            });
            SidecarService.SaveAsync(sidecar).GetAwaiter().GetResult();

            var json = File.ReadAllText(sidecar.SidecarPath);
            foreach (var expected in new[] { "\"schemaVersion\": 1", "\"timestampMs\": 763000", "\"startMs\": 1103000", "\"endMs\": 1145000", "müller" })
                if (!json.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Sidecar-JSON enthält „{expected}“ nicht.");

            var reloaded = SidecarService.LoadAsync(media, null).GetAwaiter().GetResult();
            if (!reloaded.Existed || reloaded.Markers.Count != 2)
                throw new InvalidOperationException("Sidecar-Rundlauf verliert Marker.");
            var point = reloaded.Markers.First(m => !m.IsRange);
            var range = reloaded.Markers.First(m => m.IsRange);
            if (point.StartMs != 763_000 || point.Title != "Tor Müller" || point.Tags.Count != 3 || point.Category != "Highlight")
                throw new InvalidOperationException("Punkt-Marker wird falsch gelesen.");
            if (range.StartMs != 1_103_000 || range.EndMs != 1_145_000 || range.DurationMs != 42_000)
                throw new InvalidOperationException("Bereich-Marker wird falsch gelesen.");
            if (reloaded.MediaId != sidecar.MediaId || reloaded.TagsText != "#bundesliga #vfb")
                throw new InvalidOperationException("Kopfdaten der Sidecar-Datei gehen verloren.");

            var withExtra = json.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"kiZusammenfassung\": \"Testwert\",", StringComparison.Ordinal);
            File.WriteAllText(reloaded.SidecarPath, withExtra);
            var third = SidecarService.LoadAsync(media, null).GetAwaiter().GetResult();
            SidecarService.SaveAsync(third).GetAwaiter().GetResult();
            if (!File.ReadAllText(third.SidecarPath).Contains("kiZusammenfassung", StringComparison.Ordinal))
                throw new InvalidOperationException("Unbekannte Felder der Sidecar-Datei gehen beim Speichern verloren.");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private static void RunServiceChecks(string outputRoot)
    {
        if (Math.Abs((Formatters.ParseTime("01:02:03.500") ?? 0) - 3723.5) > .001) throw new InvalidOperationException("Zeitparser fehlgeschlagen.");
        if (Formatters.Atempo(4).Split(',').Length != 2) throw new InvalidOperationException("atempo-Kette fehlgeschlagen.");
        var ffmpeg = new FfmpegService();
        if (!ffmpeg.IsAvailable) return;
        var directory = Path.Combine(outputRoot, "functional");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "sample.mp4");
        var cut = Path.Combine(directory, "sample-cut.mp4");
        var create = ffmpeg.RunAsync([
            "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25:d=1.2",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=1.2", "-shortest",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source
        ]).GetAwaiter().GetResult();
        if (create != 0 || !File.Exists(source)) throw new InvalidOperationException("FFmpeg-Testvideo konnte nicht erzeugt werden.");
        var info = ffmpeg.ProbeAsync(source).GetAwaiter().GetResult();
        if (!info.Ok || !info.HasVideo || !info.HasAudio || info.Width != 320 || info.Height != 180) throw new InvalidOperationException("FFprobe-Auswertung fehlgeschlagen.");
        var frame = ffmpeg.ExtractFrameAsync(source, .4, 160).GetAwaiter().GetResult();
        if (frame is null || frame.Length < 100) throw new InvalidOperationException("Frame-Vorschau fehlgeschlagen.");
        var cutCode = ffmpeg.RunAsync(["-y", "-ss", "0.2", "-i", source, "-t", "0.5", "-c", "copy", cut], .5).GetAwaiter().GetResult();
        if (cutCode != 0 || !File.Exists(cut)) throw new InvalidOperationException("Cut-Prozessprüfung fehlgeschlagen.");

        RunEditorChecks(ffmpeg, directory, source);

        var ytDlp = new YtDlpService();
        if (ytDlp.ExecutablePath is not null)
        {
            var version = ProcessRunner.CaptureAsync(ytDlp.ExecutablePath, ["--version"]).GetAwaiter().GetResult();
            var sandboxBlocked = version.StandardError.Contains("Failed to extract", StringComparison.OrdinalIgnoreCase);
            if (!sandboxBlocked && (version.ExitCode != 0 || string.IsNullOrWhiteSpace(version.StandardOutput)))
                throw new InvalidOperationException("yt-dlp-Prozessprüfung fehlgeschlagen.");
        }
    }

    /// <summary>Fährt den Editor einmal komplett durch: laden, teilen, Marker, Sidecar, Export.</summary>
    private static void RunEditorChecks(FfmpegService ffmpeg, string directory, string source)
    {
        var media = Path.Combine(directory, "editor-sample.mp4");
        File.Copy(source, media, overwrite: true);
        var sidecarPath = SidecarService.PathFor(media);
        if (File.Exists(sidecarPath)) File.Delete(sidecarPath);

        var vm = new EditorViewModel(ffmpeg, () => { });
        var load = typeof(EditorViewModelBase).GetMethod("LoadMediaAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Der Medien-Ladevorgang wurde nicht gefunden.");
        var loading = Dispatcher.CurrentDispatcher.InvokeAsync(async () => await (Task)load.Invoke(vm, [media])!).Task.Unwrap();
        Pump(() => loading.IsCompleted, TimeSpan.FromMinutes(2));
        loading.GetAwaiter().GetResult();

        if (vm.Sidecar is null || vm.Clips.Count != 1 || vm.Clips[0].DurationMs < 1000)
            throw new InvalidOperationException("Der Editor legt die geladene Datei nicht als einen Clip an.");

        vm.Position = .5;
        vm.SplitClipCommand.Execute(null);
        if (vm.Clips.Count != 2 || Math.Abs(vm.Clips[0].EndMs - 500) > 5 || vm.Clips[1].StartMs != vm.Clips[0].EndMs)
            throw new InvalidOperationException("Das Teilen am Abspielkopf fehlgeschlagen.");

        vm.AddMarker(new MediaMarker
        {
            Kind = MarkerKind.Range, StartMs = 100, EndMs = 400,
            Title = "Testbereich", Category = "Highlight", Tags = SidecarService.NormalizeTags("test highlight")
        });
        vm.SaveSidecarAsync(manual: true).GetAwaiter().GetResult();
        if (!File.Exists(sidecarPath) || !File.ReadAllText(sidecarPath).Contains("Testbereich", StringComparison.Ordinal))
            throw new InvalidOperationException("Der Marker landet nicht in der Sidecar-Datei.");
        if (!vm.Markers[0].Id.StartsWith("range-", StringComparison.Ordinal))
            throw new InvalidOperationException("Bereich-Marker bekommen keine passende Id.");

        vm.ClipFromMarkerCommand.Execute(vm.Markers[0]);
        if (vm.Clips.Count != 3 || vm.Clips[2].DurationMs != 300 || vm.Clips[2].Number != 3)
            throw new InvalidOperationException("Ein Bereich-Marker wird nicht als Clip übernommen.");

        var exportTarget = Path.Combine(directory, "editor-export.mp4");
        if (File.Exists(exportTarget)) File.Delete(exportTarget);
        vm.Exact = true;
        vm.OutputFolder = directory;
        vm.OutputName = "editor-export.mp4";
        vm.ExportCommand.Execute(null);
        Pump(() => !vm.IsBusy, TimeSpan.FromMinutes(3));
        if (!File.Exists(exportTarget))
            throw new InvalidOperationException("Der Editor-Export erzeugt keine Datei: " + vm.Status);
        var info = ffmpeg.ProbeAsync(exportTarget).GetAwaiter().GetResult();
        if (!info.Ok || info.Duration < 1)
            throw new InvalidOperationException("Die exportierte Datei ist unbrauchbar.");
    }

    /// <summary>Lässt die Nachrichtenschleife laufen, bis eine asynchrone Aktion fertig ist.</summary>
    private static void Pump(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(25);
        }
        if (!done()) throw new TimeoutException("Ein Vorgang wurde nicht rechtzeitig fertig.");
    }

    private static void RunThumbnailEmbeddingChecks()
    {
        var vm = new DownloadViewModel(new FfmpegService(), new YtDlpService(), () => { });
        if (vm.EmbedThumbnail) throw new InvalidOperationException("Thumbnail-Einbettung ist nicht standardmäßig deaktiviert.");
        if (vm.CanConvertToH264) throw new InvalidOperationException("Die zusätzliche H.264-Konvertierung muss bei aktiver H.264-Priorität gesperrt sein.");
        vm.PreferH264 = false;
        if (!vm.CanConvertToH264) throw new InvalidOperationException("Die zusätzliche H.264-Konvertierung muss für MP4 ohne H.264-Priorität wählbar sein.");
        vm.Format = "MKV";
        if (vm.CanConvertToH264) throw new InvalidOperationException("Die zusätzliche H.264-Konvertierung darf nur für MP4 wählbar sein.");
        vm.Format = "MP4";
        vm.PreferH264 = true;

        var job = new DownloadJob(1, "https://www.youtube.com/watch?v=test", "video", string.Empty,
            "Best", "MP4", "Original", "CPU (Standard)", "Aus", keepOriginal: true, embedThumbnail: true);
        if (!job.CloneForRetry(2).EmbedThumbnail)
            throw new InvalidOperationException("Thumbnail-Einbettung geht beim Wiederholen verloren.");

        var method = typeof(DownloadViewModel).GetMethod("BuildBaseArguments", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("yt-dlp-Argumentaufbau nicht gefunden.");
        var arguments = (List<string>?)method.Invoke(vm, [job, "output.mp4", "Keine"])
            ?? throw new InvalidOperationException("yt-dlp-Argumentaufbau lieferte kein Ergebnis.");
        var formatIndex = arguments.IndexOf("-f");
        if (formatIndex < 0 || !arguments[formatIndex + 1].Contains("vcodec~='^(avc|h264)'", StringComparison.Ordinal))
            throw new InvalidOperationException("MP4 muss standardmaessig H.264 bevorzugen.");
        var bestJob = new DownloadJob(2, "https://www.youtube.com/watch?v=test", "video", string.Empty,
            "Best", "MP4", "Original", "CPU (Standard)", "Aus", keepOriginal: true, preferH264: false);
        var bestArguments = (List<string>?)method.Invoke(vm, [bestJob, "output.mp4", "Keine"])
            ?? throw new InvalidOperationException("yt-dlp-Argumentaufbau fuer die beste Qualitaet lieferte kein Ergebnis.");
        var bestFormatIndex = bestArguments.IndexOf("-f");
        if (bestFormatIndex < 0 || !bestArguments[bestFormatIndex + 1].StartsWith("bv*+ba/b", StringComparison.Ordinal))
            throw new InvalidOperationException("Ohne H.264-Prioritaet muss der beste Video- und Audiostream gewaehlt werden.");
        if (bestJob.ConvertToH264)
            throw new InvalidOperationException("Die teure H.264-Neukodierung darf nicht implizit aktiviert sein.");
        if (!arguments.Contains("--embed-thumbnail", StringComparer.Ordinal))
            throw new InvalidOperationException("Die Thumbnail-Einbettung wird nicht an yt-dlp übergeben.");
    }
}
