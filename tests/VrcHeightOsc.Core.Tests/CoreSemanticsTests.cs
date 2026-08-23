using System.Text.Json;
using VrcHeightOsc.Core.Config;
using VrcHeightOsc.Core.Domain;
using Xunit;

namespace VrcHeightOsc.Core.Tests;

public sealed class CoreSemanticsTests
{
    [Fact]
    public void NormalizationMatchesOscParameterRules()
    {
        Assert.Equal("Track Headpats", ValueNormalization.NormalizeParamName("/avatar/parameters/Track%20Headpats"));
        Assert.Equal("Track Headpats", ValueNormalization.NormalizeParamName(" Track%20Headpats "));
        Assert.True(ValueNormalization.Boolish(new[] { new[] { 1.0f } }));
        Assert.Equal(1.25d, ValueNormalization.SafeFloat(new[] { new[] { "1.25" } }));
    }

    [Fact]
    public void LimitModesMatchPythonBehavior()
    {
        var rule = new RuleDefinition { LimitEnabled = true, LimitMin = 0.5, LimitMax = 2.0, LimitBehavior = "clamp" };
        Assert.Equal(2.0, HeightMath.ApplyRuleHeightLimits(rule, 1.0, 3.0));

        rule.LimitBehavior = "block_outside";
        Assert.Null(HeightMath.ApplyRuleHeightLimits(rule, 0.25, 0.1));
        Assert.Null(HeightMath.ApplyRuleHeightLimits(rule, 0.25, 1.0));

        rule.LimitBehavior = "toward_range";
        Assert.Null(HeightMath.ApplyRuleHeightLimits(rule, 0.25, 0.1));
        Assert.Equal(0.5, HeightMath.ApplyRuleHeightLimits(rule, 0.25, 0.4));
    }

    [Fact]
    public void TriggerRuleHonorsRisingEdgeAndCooldown()
    {
        var state = new AppState();
        state.UpdateValue(OscPaths.EyeHeight, 1.0f);
        state.ReplaceRules(new[]
        {
            new RuleDefinition
            {
                Parameter = "Track",
                HeightValue = 0.1,
                Action = "add",
                Cooldown = 0.5,
                RisingEdgeOnly = true,
            },
        });

        var engine = new RuleEngine();
        var first = engine.Evaluate("/avatar/parameters/Track", true, state.Snapshot(), 10.0);
        Assert.Single(first.Commands);
        Assert.Equal(1.1, first.Commands[0].Height, precision: 8);

        var held = engine.Evaluate("/avatar/parameters/Track", true, state.Snapshot(), 11.0);
        Assert.Empty(held.Commands);

        _ = engine.Evaluate("/avatar/parameters/Track", false, state.Snapshot(), 12.0);
        var rising = engine.Evaluate("/avatar/parameters/Track", true, state.Snapshot(), 13.0);
        Assert.Single(rising.Commands);
    }

    [Fact]
    public void FollowRuleUsesDeadbandAndMapping()
    {
        var state = new AppState();
        state.UpdateValue(OscPaths.EyeHeight, 1.0f);
        state.ReplaceRules(new[]
        {
            new RuleDefinition
            {
                Parameter = "Blend",
                Mode = "follow",
                FollowInputMin = 0.0,
                FollowInputMax = 1.0,
                FollowHeightMin = 0.5,
                FollowHeightMax = 2.0,
                FollowDeadband = 0.1,
            },
        });

        var engine = new RuleEngine();
        var first = engine.Evaluate("/avatar/parameters/Blend", 0.5f, state.Snapshot(), 1.0);
        Assert.Single(first.Commands);
        Assert.Equal(1.25, first.Commands[0].Height, precision: 8);

        var insideDeadband = engine.Evaluate("/avatar/parameters/Blend", 0.55f, state.Snapshot(), 2.0);
        Assert.Empty(insideDeadband.Commands);

        var outsideDeadband = engine.Evaluate("/avatar/parameters/Blend", 0.8f, state.Snapshot(), 3.0);
        Assert.Single(outsideDeadband.Commands);
        Assert.Equal(1.7, outsideDeadband.Commands[0].Height, precision: 6);
    }

    [Fact]
    public void ConfigStoreReadsPythonV3AndSavesRuntimeResetAtomically()
    {
        var directory = Directory.CreateTempSubdirectory("vrc-height-core-test");
        try
        {
            var path = System.IO.Path.Combine(directory.FullName, "vrc_height_osc_config.json");
            File.WriteAllText(path, """
            {
              "version": 3,
              "ui": {
                "geometry": "800x600",
                "height_value": 0.12,
                "smooth_enabled": true,
                "smooth_time": 0.35,
                "custom_ui": "preserved"
              },
              "rules": [{
                "enabled": true,
                "parameter": "Track%20Headpats",
                "height_value": 1.6,
                "mode": "trigger",
                "condition": "true",
                "threshold": 0.5,
                "action": "set",
                "cooldown": 1.0,
                "rising_edge_only": true,
                "smooth_enabled": false,
                "smooth_time": 0.35,
                "limit_enabled": false,
                "limit_min": 0.5,
                "limit_max": 2.0,
                "limit_behavior": "clamp",
                "follow_input_min": 0.0,
                "follow_input_max": 1.0,
                "follow_height_min": 0.5,
                "follow_height_max": 2.0,
                "follow_deadband": 0.005,
                "last_fire": 99.0,
                "was_active": true,
                "last_follow_value": 1.0,
                "last_follow_height": 2.0
              }]
            }
            """);

            using var store = new JsonConfigStore(path);
            var config = store.Load();
            Assert.Null(store.LastError);
            Assert.Equal("Track Headpats", config.Rules[0].Parameter);
            Assert.Equal(0.0, config.Rules[0].LastFire);
            Assert.False(config.Rules[0].WasActive);
            Assert.Equal("0.35", config.Ui.SmoothTime);

            store.SaveNow(config);
            Assert.Null(store.LastError);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal(3, root.GetProperty("version").GetInt32());
            Assert.Equal("custom_ui", root.GetProperty("ui").EnumerateObject().Single(property => property.Name == "custom_ui").Name);
            Assert.Equal(0.0, root.GetProperty("rules")[0].GetProperty("last_fire").GetDouble());
            Assert.False(root.GetProperty("rules")[0].GetProperty("was_active").GetBoolean());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
