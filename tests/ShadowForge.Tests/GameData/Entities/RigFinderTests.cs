using ShadowForge.Formats.HDB;
using ShadowForge.GameData.Entities;

namespace ShadowForge.Tests.GameData.Entities;

public sealed class RigFinderTests
{
    [Fact]
    public void Matches_SameNamesInAnyOrder_LeavingOutPlaceholderJoints()
    {
        Assert.True(RigFinder.Matches(["neck", "Dummy", "hips", "head"], ["hips", "neck", "head"]));
    }

    [Fact]
    public void Matches_FailsOnAMissingOrAnExtraBone()
    {
        Assert.False(RigFinder.Matches(["hips", "neck"], ["hips", "neck", "head"]));
        Assert.False(RigFinder.Matches(["hips", "neck", "head", "cap"], ["hips", "neck", "head"]));
    }

    [Fact]
    public void SameRestPose_ComparesEveryRestValueByName()
    {
        var rig = new List<Bone>
        {
            new() { Name = "hips", PosY = 10f },
            new() { Name = "neck", ExtraEuler = [0f, 0f, 1f, 0f, 0f, 0f] },
        };
        var reordered = new List<Bone>
        {
            new() { Name = "neck", ExtraEuler = [0f, 0f, 1f, 0f, 0f, 0f] },
            new() { Name = "Dummy" },
            new() { Name = "hips", PosY = 10f },
        };
        var turned = new List<Bone>
        {
            new() { Name = "hips", PosY = 10f },
            new() { Name = "neck", ExtraEuler = [0f, 0f, 2f, 0f, 0f, 0f] },
        };

        Assert.True(RigFinder.SameRestPose(rig, reordered));
        Assert.False(RigFinder.SameRestPose(rig, turned));
    }

    [SkippableFact]
    public void Find_Shipped_Pc03_ByItsBoneNames()
    {
        var pc03 = ModelCooker.Bake(ModelReader.Read(RetailData.Read(@"chara\ply\pc03\pc03_obj.hdb")));

        var match = Assert.Single(new RigFinder(RetailData.Install).Find(pc03.Bones.Select(b => b.Name).ToList()));

        Assert.Equal(@"chara\ply\pc03\pc03_obj.hdb", match.VfsPath);
    }
}
