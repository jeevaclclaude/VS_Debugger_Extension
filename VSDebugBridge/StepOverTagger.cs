using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace VSDebugBridge
{
    /// <summary>
    /// Tags lines that the user has marked "step over for Claude". The glyph
    /// factory (StepOverGlyphFactory) turns each tag into the teal "SO" circle
    /// drawn in the indicator margin.
    ///
    /// Bridge to BridgeServer: BridgeServer.StepFiltersChanged is a static event
    /// raised whenever the filter list changes (via right-click or on solution
    /// load). The tagger subscribes on construction and fires TagsChanged so VS
    /// redraws the margin immediately without waiting for a scroll/paint cycle.
    /// </summary>
    internal class StepOverTagger : ITagger<StepOverTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private string _filePath;
        private bool _disposed;

        public StepOverTagger(ITextBuffer buffer, ITextDocumentFactoryService textDocFactory)
        {
            _buffer = buffer;
            if (textDocFactory.TryGetTextDocument(buffer, out var doc))
            {
                _filePath = doc.FilePath;
                doc.FileActionOccurred += OnFileActionOccurred;
            }
            BridgeServer.StepFiltersChanged += OnStepFiltersChanged;
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<StepOverTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (string.IsNullOrEmpty(_filePath) || BridgeServer.Instance == null)
                yield break;

            foreach (var span in spans)
            {
                var snapshot = span.Snapshot;
                int firstLineIndex = snapshot.GetLineNumberFromPosition(span.Start.Position);
                int lastLineIndex = snapshot.GetLineNumberFromPosition(span.End == span.Start ? span.Start.Position : Math.Max(span.Start.Position, span.End.Position - 1));

                for (int lineIndex = firstLineIndex; lineIndex <= lastLineIndex; lineIndex++)
                {
                    int vsLine = lineIndex + 1; // ITextSnapshot is 0-based; VS/bridge uses 1-based
                    if (BridgeServer.Instance.IsStepFilter(_filePath, vsLine))
                    {
                        var textLine = snapshot.GetLineFromLineNumber(lineIndex);
                        yield return new TagSpan<StepOverTag>(
                            new SnapshotSpan(textLine.Start, textLine.End),
                            new StepOverTag());
                    }
                }
            }
        }

        private void OnStepFiltersChanged(object sender, EventArgs e)
        {
            // Raise TagsChanged covering the entire buffer so VS re-queries all
            // visible lines and redraws margin glyphs. Lightweight — VS only
            // redraws the visible viewport, not the whole file.
            var snapshot = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            if (sender is ITextDocument doc)
                _filePath = doc.FilePath;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            BridgeServer.StepFiltersChanged -= OnStepFiltersChanged;
        }
    }

    /// <summary>
    /// MEF export that creates a StepOverTagger for every code-content text buffer.
    /// VS discovers this at startup and calls CreateTagger once per IWpfTextView.
    /// </summary>
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("code")]
    [TagType(typeof(StepOverTag))]
    internal sealed class StepOverTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal ITextDocumentFactoryService TextDocumentFactory { get; set; }

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (buffer == null) return null;
            // One tagger per buffer — buffer.Properties caches it so it isn't
            // recreated on every view scroll or window split.
            return buffer.Properties.GetOrCreateSingletonProperty(
                () => new StepOverTagger(buffer, TextDocumentFactory)) as ITagger<T>;
        }
    }
}
