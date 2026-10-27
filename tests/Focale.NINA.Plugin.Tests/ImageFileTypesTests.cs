using FluentAssertions;
using Focale.NINA.Plugin.Common;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class ImageFileTypesTests {
    [TestCase(@"C:\Images\M31_LIGHT_0001.fits")]
    [TestCase(@"C:\Images\M31_LIGHT_0001.FITS")]
    [TestCase(@"C:\Images\dark.fit")]
    [TestCase(@"C:\Images\bias.fts")]
    [TestCase(@"C:\Images\M31_LIGHT_0001.xisf")]
    [TestCase(@"C:\Images\M31_LIGHT_0001.XISF")]
    public void FitsAndXisf_AreSupported(string path) {
        ImageFileTypes.IsSupported(path).Should().BeTrue();
    }

    [TestCase(@"C:\Images\M31_LIGHT_0001.tif")]
    [TestCase(@"C:\Images\M31_LIGHT_0001.tiff")]
    [TestCase(@"C:\Images\M31_LIGHT_0001.cr2")]
    [TestCase(@"C:\Images\M31_LIGHT_0001.png")]
    [TestCase(@"C:\Images\M31_LIGHT_0001")]
    [TestCase("")]
    [TestCase(null)]
    public void OtherFormats_AreSkipped(string? path) {
        ImageFileTypes.IsSupported(path).Should().BeFalse();
    }

    [Test]
    public void ContentType_FollowsTheFormat() {
        ImageFileTypes.ContentTypeFor("a.fits").Should().Be("image/fits");
        ImageFileTypes.ContentTypeFor("a.xisf").Should().Be("application/octet-stream");
    }
}
