using FluentAssertions;
using Focale.NINA.Plugin.Api;
using Focale.NINA.Plugin.Common;
using NUnit.Framework;

namespace Focale.NINA.Plugin.Tests;

[TestFixture]
public class FrameClassifierTests {
    [TestCase("LIGHT")]
    [TestCase("light")]
    [TestCase("Light")]
    [TestCase(" Snapshot ")]
    public void Lights_ClassifyAsObservation(string imageType) {
        FrameClassifier.FromNinaImageType(imageType).Should().Be(FrameClass.Observation);
    }

    [TestCase("DARK")]
    [TestCase("BIAS")]
    [TestCase("FLAT")]
    [TestCase("DarkFlat")]
    [TestCase("FlatDark")]
    [TestCase("BIASDARK")]
    public void Calibrations_ClassifyAsCalibration(string imageType) {
        FrameClassifier.FromNinaImageType(imageType).Should().Be(FrameClass.Calibration);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("UNKNOWN")]
    public void Empty_Or_Unknown_DefaultToObservation(string? imageType) {
        FrameClassifier.FromNinaImageType(imageType).Should().Be(FrameClass.Observation);
    }
}
