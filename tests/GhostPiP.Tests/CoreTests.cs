using GhostPiP.Core;
using Xunit;

namespace GhostPiP.Tests;

public class SmartCropTests
{
    static Frame Solid(int w, int h, byte v)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, v);
        return new Frame(w, h, px);
    }

    static void Fill(Frame f, int x0, int y0, int x1, int y1, byte v)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                for (int c = 0; c < 3; c++)
                    f.Pixels[(y * f.Width + x) * 4 + c] = v;
    }

    [Fact]
    public void FindMotion_boxes_the_part_that_changes()
    {
        var a = Solid(1280, 720, 20);
        var b = Solid(1280, 720, 20);
        Fill(a, 320, 180, 960, 540, 60);
        Fill(b, 320, 180, 960, 540, 200);
        var box = SmartCrop.FindMotion(new[] { a, b });
        Assert.NotNull(box);
        Assert.Equal(0.25, box![0], 2);
        Assert.Equal(0.25, box[1], 2);
        Assert.Equal(0.5, box[2], 2);
        Assert.Equal(0.5, box[3], 2);
    }

    [Fact]
    public void FindMotion_ignores_a_still_window_and_a_mid_scan_resize()
    {
        Assert.Null(SmartCrop.FindMotion(new[] { Solid(640, 360, 9), Solid(640, 360, 9) }));
        Assert.Null(SmartCrop.FindMotion(new[] { Solid(640, 360, 9), Solid(320, 180, 9) }));
    }

    [Fact]
    public void StrongestEdge_finds_the_exact_pixel()
    {
        var f = Solid(200, 200, 10);
        Fill(f, 0, 57, 200, 200, 220);
        Assert.Equal(57, SmartCrop.StrongestEdge(f, Side.Top, 50, 0, 200));
        Assert.Null(SmartCrop.StrongestEdge(Solid(200, 200, 10), Side.Top, 50, 0, 200));
    }

    [Fact]
    public void ShaveFlat_removes_letterbox_bars()
    {
        var f = Solid(200, 200, 0);
        for (int y = 30; y < 170; y++)
            for (int x = 0; x < 200; x++)
                f.Pixels[(y * 200 + x) * 4] = (byte)(x * 255 / 200);
        var edges = new[] { 0, 0, 200, 200 };
        Assert.Equal(30, SmartCrop.ShaveFlat(f, Side.Top, edges, 16));
        Assert.Equal(170, SmartCrop.ShaveFlat(f, Side.Bottom, edges, 16));
    }

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(1000, 1000, "1:1")]
    [InlineData(1280, 800, "1.600:1")]
    public void RatioLabel_names_familiar_shapes(int w, int h, string expected) =>
        Assert.Equal(expected, SmartCrop.RatioLabel(w, h));
}

public class SettingsTests
{
    const string V1 = """
    { "opacity": 1.0, "crop": [0.2787, 0.1629, 0.4426, 0.7274], "geometry": [1730, 16, 174],
      "scale": 0.41, "toolbar": "hover", "match": "youtube" }
    """;

    [Fact]
    public void V1_file_moves_its_crop_to_the_keyword_site()
    {
        var s = SettingsStore.Parse(V1);
        Assert.Equal(2, s.Version);
        var p = s.Profiles["site:youtube"];
        Assert.Equal(0.2787, p.Crop[0]);
        Assert.Equal(0.41, p.Scale);
        Assert.Equal(new[] { 1730, 16 }, p.Position);
        Assert.Equal("youtube", p.TabKeyword);
        Assert.Equal(new double[] { 0, 0, 1, 1 }, s.Defaults.Crop);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"version\": \"banana\" }")]
    public void Damaged_files_fall_back_to_defaults(string text) =>
        Assert.Equal("hover", SettingsStore.Parse(text).Controller);

    [Fact]
    public void Bad_values_are_repaired()
    {
        var p = new MirrorProfile { Opacity = 9, Crop = new double[] { 0.9, 0, 0.9, 1 }, Corners = "wavy" };
        p.Sanitize();
        Assert.Equal(1.0, p.Opacity);
        Assert.Equal(new double[] { 0, 0, 1, 1 }, p.Crop);
        Assert.Equal("round", p.Corners);
    }
}

public class WindowTitleTests
{
    [Theory]
    [InlineData("firefox.exe", "(3) Aurora - YouTube — Mozilla Firefox", "YouTube")]
    [InlineData("chrome.exe", "Stream - Twitch - Google Chrome", "Twitch")]
    [InlineData("firefox.exe", "Dan - Hardcore History — Mozilla Firefox", "")]
    [InlineData("vlc.exe", "Movie - YouTube - VLC", "")]
    public void SiteOf_only_trusts_known_sites_in_browsers(string exe, string title, string site) =>
        Assert.Equal(site, WindowScanner.SiteOf(exe, title));

    [Fact]
    public void Profile_key_is_shared_across_browsers()
    {
        Assert.Equal("site:youtube", WindowScanner.ProfileKey("firefox.exe", "A - YouTube — Mozilla Firefox"));
        Assert.Equal("site:youtube", WindowScanner.ProfileKey("chrome.exe", "A - YouTube - Google Chrome"));
        Assert.Equal("app:vlc.exe", WindowScanner.ProfileKey("vlc.exe", "Movie"));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Up")]
    [InlineData("Ctrl+Alt+=")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("Alt+Space")]
    public void Chords_round_trip(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(text, chord.ToString());
    }

    [Fact]
    public void Defaults_are_all_valid_and_unique()
    {
        var chords = HotkeyManager.All.Select(d => HotkeyManager.ChordFor(d, new Dictionary<string, string>())).ToList();
        Assert.All(chords, c => Assert.NotEqual(0, c.Vk));
        Assert.Equal(chords.Count, chords.Distinct().Count());
    }
}
