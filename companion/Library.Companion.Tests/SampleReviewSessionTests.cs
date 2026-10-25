using System.Text;
using Xunit;
namespace Library.Companion.Tests;
public class SampleReviewSessionTests
{
    static SampleReviewSession Sample() { var s = new SampleReviewSession(); s.Output.Write(Encoding.UTF8.GetBytes("invented fixture")); return s; }
    [Fact] public void OutputLivesUntilCompleteDecision()
    { using var s = Sample(); var hash = s.Seal(); s.TechnicalPassed(hash); Assert.NotEmpty(s.Read().ToArray()); s.VisualReview(hash, new string('a', 64), true, "Invented comparison reviewed."); s.Complete(); Assert.Equal("accepted-local-sample", s.Decision); Assert.NotEmpty(s.Read().ToArray()); }
    [Fact] public void SkippedChecksCannotAccept()
    { using var s = Sample(); var hash = s.Seal(); Assert.Throws<IOException>(() => s.Complete()); Assert.Throws<IOException>(() => s.VisualReview(hash, new string('a', 64), true, "skip")); s.TechnicalPassed(hash); Assert.Throws<IOException>(() => s.Complete()); }
    [Fact] public void ChangedOutputOrStaleHashCannotAccept()
    { using var s = Sample(); var writer = s.Output; var hash = s.Seal(); Assert.Throws<IOException>(() => s.TechnicalPassed(new string('a', 64))); s.TechnicalPassed(hash); s.VisualReview(hash, new string('a', 64), true, "reviewed"); writer.WriteByte(1); Assert.Throws<IOException>(() => s.Complete()); }
    [Fact] public void VisualRejectionIsTerminal()
    { using var s = Sample(); var hash = s.Seal(); s.TechnicalPassed(hash); s.VisualReview(hash, new string('a', 64), false, "Visible defect in fixture."); s.Complete(); Assert.Equal("rejected-local-sample", s.Decision); Assert.Throws<IOException>(() => s.Complete()); }
    [Fact] public void DisposedIncompleteSessionCannotBecomeAccepted()
    { var s = Sample(); s.Seal(); s.Dispose(); Assert.Equal("rejected-incomplete-session", s.Decision); Assert.Throws<ObjectDisposedException>(() => s.Complete()); }
}
