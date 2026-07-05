using Xunit;

namespace WyvrnChroma.Tests;

public class WyvrnConfigTests
{
    // Synthetic wyvrn.config (no Razer content) mirroring the real schema's shape.
    private const string Sample = """
    {
      "ExternalCommands": [
        {
          "External_Command_ID": "Crouch_On",
          "Haptic_Events": [ { "Haptic_Effect": "Crouch_On" } ],
          "Chroma_Events": [
            { "Chroma_Effect": "Crouch_On_Keyboard", "Animation": "Crouch_On", "Interrupt": true, "Priority": "Medium" },
            { "Chroma_Effect": "Crouch_On_Mouse", "Animation": "Crouch_On" }
          ]
        },
        {
          "External_Command_ID": "Recon_On",
          "Chroma_Events": [
            { "Chroma_Effect": "Recon_On_Keyboard", "Animation": "Recon_On", "Loop": "infinity", "Push": true },
            { "Chroma_Effect": "Recon_On_ChromaLink", "Animation": "Recon_On", "Priority": "VeryHigh", "Idle": "set" }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_MapsEventsToPerDeviceEffects()
    {
        var cfg = WyvrnConfig.Parse(Sample);

        Assert.Equal(2, cfg.Events.Count);
        Assert.Contains("Crouch_On", cfg.Events);
        Assert.Contains("Recon_On", cfg.Events);

        var crouch = cfg.GetEffects("Crouch_On");
        Assert.Equal(2, crouch.Count);
    }

    [Fact]
    public void GetEffect_ByDevice_ReturnsTheRightEntry()
    {
        var cfg = WyvrnConfig.Parse(Sample);

        var kb = cfg.GetEffect("Crouch_On", ChromaDevice.Keyboard);
        Assert.NotNull(kb);
        Assert.Equal("Crouch_On_Keyboard", kb!.ChromaEffect);
        Assert.Equal("Crouch_On", kb.Animation);
        Assert.True(kb.Interrupt);
        Assert.Equal(WyvrnPriority.Medium, kb.Priority);
        Assert.False(kb.Loops);
        Assert.False(kb.Push);
        Assert.False(kb.Idle);

        var mouse = cfg.GetEffect("Crouch_On", ChromaDevice.Mouse);
        Assert.NotNull(mouse);
        Assert.Equal(ChromaDevice.Mouse, mouse!.Device);
        Assert.False(mouse.Interrupt);
        Assert.Null(mouse.Priority);
    }

    [Fact]
    public void Parse_InterpretsLoopPushIdleAndPriority()
    {
        var cfg = WyvrnConfig.Parse(Sample);

        var kb = cfg.GetEffect("Recon_On", ChromaDevice.Keyboard)!;
        Assert.True(kb.Loops); // Loop == "infinity"
        Assert.True(kb.Push);

        var link = cfg.GetEffect("Recon_On", ChromaDevice.ChromaLink)!;
        Assert.Equal(ChromaDevice.ChromaLink, link.Device); // multi-word device token
        Assert.Equal(WyvrnPriority.VeryHigh, link.Priority);
        Assert.True(link.Idle); // Idle == "set"
    }

    [Fact]
    public void GetEffect_UnknownEventOrDevice_ReturnsNullOrEmpty()
    {
        var cfg = WyvrnConfig.Parse(Sample);

        Assert.Empty(cfg.GetEffects("Does_Not_Exist"));
        Assert.Null(cfg.GetEffect("Does_Not_Exist", ChromaDevice.Keyboard));
        Assert.Null(cfg.GetEffect("Crouch_On", ChromaDevice.Headset)); // event exists, device not bound
    }

    [Fact]
    public void Parse_SkipsEffectsWithoutARecognisedDeviceSuffix()
    {
        const string json = """
        {
          "ExternalCommands": [
            {
              "External_Command_ID": "Weird",
              "Chroma_Events": [
                { "Chroma_Effect": "Something_Gamepad", "Animation": "X" },
                { "Chroma_Effect": "Good_Keyboard", "Animation": "X" }
              ]
            }
          ]
        }
        """;

        var cfg = WyvrnConfig.Parse(json);
        var effects = cfg.GetEffects("Weird");
        Assert.Single(effects);
        Assert.Equal(ChromaDevice.Keyboard, effects[0].Device);
    }

    [Fact]
    public void Parse_DuplicateCommandId_AggregatesEffects()
    {
        const string json = """
        {
          "ExternalCommands": [
            { "External_Command_ID": "Dup", "Chroma_Events": [ { "Chroma_Effect": "A_Keyboard" } ] },
            { "External_Command_ID": "Dup", "Chroma_Events": [ { "Chroma_Effect": "B_Mouse" } ] }
          ]
        }
        """;

        var cfg = WyvrnConfig.Parse(json);
        var effects = cfg.GetEffects("Dup");
        Assert.Equal(2, effects.Count);
        Assert.Equal("A_Keyboard", effects[0].ChromaEffect); // earlier entry preserved, in order
        Assert.Equal("B_Mouse", effects[1].ChromaEffect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_Throws(string json)
        => Assert.Throws<WyvrnConfigException>(() => WyvrnConfig.Parse(json));

    [Fact]
    public void Parse_InvalidJson_Throws()
        => Assert.Throws<WyvrnConfigException>(() => WyvrnConfig.Parse("{ not json "));

    [Fact]
    public void Parse_NoExternalCommands_Throws()
        => Assert.Throws<WyvrnConfigException>(() => WyvrnConfig.Parse("""{ "Other": 1 }"""));

    // Guarded integration check against a real local install; skips cleanly when the catalog is not present.
    [Fact]
    public void Load_RealConfig_ParsesIfPresent()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\Interhaptics\hapticFolders\007 First Light\wyvrn.config",
            "/mnt/c/Program Files (x86)/Interhaptics/hapticFolders/007 First Light/wyvrn.config",
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
            return; // no local catalog on this machine — nothing to verify

        var cfg = WyvrnConfig.Load(path);

        Assert.NotEmpty(cfg.Events);
        // Independent property: a known event resolves to a keyboard .chroma stem ending in "_Keyboard".
        var crouch = cfg.GetEffect("Crouch_Off", ChromaDevice.Keyboard);
        Assert.NotNull(crouch);
        Assert.EndsWith("_Keyboard", crouch!.ChromaEffect);
    }
}
