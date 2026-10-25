using ItemLookMerger.Core;
using Newtonsoft.Json;

namespace ItemLookMerger.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ForwardNames\":true,\"VerboseLogging\":false}")]
    [InlineData("{\"ContentMerging\":{\"AllowMerging\":false}}")]
    public void A_settings_file_without_the_type_discrepancy_key_gets_the_default(string json)
    {
        var settings = JsonConvert.DeserializeObject<Settings>(json)!;

        Assert.Equal(TypeDiscrepancyHandling.TakeVisualModWeaponType, settings.InCaseOfTypeDiscrepancy);
        Assert.Equal(TypeDiscrepancyHandling.TakeVisualModWeaponType, new Settings().InCaseOfTypeDiscrepancy);
        // Also the zero value, so a missing value can never mean another option.
        Assert.Equal(0, (int)TypeDiscrepancyHandling.TakeVisualModWeaponType);
    }

    [Theory]
    [InlineData("TakeVisualModWeaponType", TypeDiscrepancyHandling.TakeVisualModWeaponType)]
    [InlineData("TakeModlistItemType", TypeDiscrepancyHandling.TakeModlistItemType)]
    [InlineData("SkipMerging", TypeDiscrepancyHandling.SkipMerging)]
    public void The_setting_is_stored_by_member_name(string name, TypeDiscrepancyHandling expected)
    {
        var settings = JsonConvert.DeserializeObject<Settings>($"{{\"InCaseOfTypeDiscrepancy\":\"{name}\"}}")!;

        Assert.Equal(expected, settings.InCaseOfTypeDiscrepancy);
    }

    [Fact]
    public void The_merge_options_default_to_the_visual_mod_weapon_type() =>
        Assert.Equal(TypeDiscrepancyHandling.TakeVisualModWeaponType, new MergeOptions([], false, false, true, null).OnTypeDiscrepancy);
}
