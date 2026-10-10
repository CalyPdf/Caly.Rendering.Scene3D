namespace Scene3D.Tests;

public class TestFilesTests
{
    [Fact]
    public void SampleScenesAreAvailable()
    {
        Assert.Equal(54, TestFiles.SceneNames.Count);

        byte[] box = TestFiles.Scene("box_base.u3d");
        Assert.True(box.Length > 4 && box[0] == 'U' && box[1] == '3' && box[2] == 'D' && box[3] == 0);
        Assert.True(File.Exists(TestFiles.ScenePath("box_base.idtf")));
    }
}