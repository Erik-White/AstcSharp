namespace AstcSharp.Encoding;

/// <summary>
/// Keeps the lowest-error candidates seen so far in a fixed-size list, sorted ascending by error.
/// Used to carry the best few partition seeds (by cheap endpoint-fit error) from the 1024-seed scan
/// into the full per-config search. Backed by caller-provided spans so it allocates nothing.
/// </summary>
internal ref struct FinalistSelector
{
    private readonly Span<int> candidates;
    private readonly Span<long> errors;

    /// <summary>
    /// Wraps the <paramref name="candidates"/>/<paramref name="errors"/> spans (equal length = the
    /// finalist capacity) and resets them to an empty list.
    /// </summary>
    public FinalistSelector(Span<int> candidates, Span<long> errors)
    {
        this.candidates = candidates;
        this.errors = errors;
        this.errors.Fill(long.MaxValue);
        this.Count = 0;
    }

    /// <summary>The number of finalists held so far (at most the span capacity).</summary>
    public int Count { get; private set; }

    /// <summary>The finalist candidate values, lowest-error first.</summary>
    public readonly ReadOnlySpan<int> Finalists => this.candidates[..this.Count];

    /// <summary>
    /// Inserts <paramref name="candidate"/>/<paramref name="error"/> in sorted position, evicting the
    /// current worst if the list is full. Does nothing if the error does not beat the worst finalist.
    /// </summary>
    public void TryInsert(int candidate, long error)
    {
        if (error >= this.errors[^1])
        {
            return;
        }

        int pos = this.errors.Length - 1;
        while (pos > 0 && this.errors[pos - 1] > error)
        {
            this.errors[pos] = this.errors[pos - 1];
            this.candidates[pos] = this.candidates[pos - 1];
            pos--;
        }

        this.errors[pos] = error;
        this.candidates[pos] = candidate;
        this.Count = Math.Min(this.Count + 1, this.candidates.Length);
    }
}
