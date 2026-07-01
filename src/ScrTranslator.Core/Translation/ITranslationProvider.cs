namespace ScrTranslator.Core.Translation;
public interface ITranslationProvider
{
    string Name { get; }
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> koreanLines, CancellationToken ct);
}
