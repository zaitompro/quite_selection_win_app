namespace QuietSelection.Core;

/// <summary>Commands that can have a global shortcut: reading commands and magnifier controls.</summary>
public enum ReadingCommand { Selection, PrimaryVoice, Region, Translate, ToggleMagnifier, InvertColors, FollowCaret, DarkenWindow, Pointer }
public enum OperationState { Idle, ReadingSelection, SelectingRegion, Recognizing, Translating, Speaking }
public enum ErrorCode
{
    None, NoSelection, SelectionUnavailable, NoFocusedElement, SelectionTimeout,
    AccessDenied, ProtectedField, TooLong, SourceChanged, ProviderFailure,
    VoiceMissing, SpeechFailure, RegionTooSmall, RegionTooLarge, CaptureFailed,
    OcrUnavailable, OcrEmpty, OcrFailure, ModelMissing, UnsupportedPair,
    SourceLanguageRequired, InvalidSettings, WorkerFailure, Cancelled,
    PackageIdentityRequired, TranslationFailure, TranslationTimeout, ModelCorrupted, ModelPreparationTimeout,
    SpeechModelMissing, SpeechModelCorrupted
}

public sealed class QuietException(ErrorCode code) : Exception(code.ToString())
{
    public ErrorCode Code { get; } = code;
}

public static class Messages
{
    public static string For(ErrorCode code) => code switch
    {
        ErrorCode.NoSelection => "Нет выделенного текста.",
        ErrorCode.SelectionUnavailable => "Приложение не предоставляет выделение. Можно отдельно выбрать чтение области экрана.",
        ErrorCode.NoFocusedElement => "Нет доступного элемента с фокусом.",
        ErrorCode.SelectionTimeout => "Истекло время получения выделения.",
        ErrorCode.AccessDenied => "Недостаточно прав для этого окна.",
        ErrorCode.ProtectedField => "Защищённое поле нельзя прочитать.",
        ErrorCode.TooLong => "Слишком большой фрагмент. Выделите не более 60 000 символов UTF-16.",
        ErrorCode.SourceChanged => "Чтение отменено: исходное окно изменилось.",
        ErrorCode.VoiceMissing => "Выбранный голос недоступен. Выберите или установите голос в разделе «Речь».",
        ErrorCode.RegionTooSmall => "Выбранная область слишком мала.",
        ErrorCode.RegionTooLarge => "Выбранная область превышает допустимый размер OCR.",
        ErrorCode.CaptureFailed => "Не удалось захватить выбранную область.",
        ErrorCode.OcrUnavailable => "Для выбранного языка не установлен компонент OCR.",
        ErrorCode.OcrEmpty => "В выбранной области текст не найден.",
        ErrorCode.ModelMissing => "Модель перевода не подготовлена. Откройте раздел «Перевод».",
        ErrorCode.ModelCorrupted => "Нарушена целостность модели. В разделе «Перевод» загрузите её повторно.",
        ErrorCode.ModelPreparationTimeout => "Подготовка модели не завершилась за 10 минут. Предыдущие модели сохранены; подготовку можно повторить.",
        ErrorCode.UnsupportedPair => "Эта языковая пара пока не поддерживается.",
        ErrorCode.TranslationFailure => "Не удалось завершить локальный перевод. Проверьте готовность моделей.",
        ErrorCode.TranslationTimeout => "Перевод не завершился за 10 минут. Попробуйте меньший фрагмент.",
        ErrorCode.SourceLanguageRequired => "Не удалось уверенно определить язык. Выберите исходный язык для этой операции.",
        ErrorCode.PackageIdentityRequired => "Для системного OCR требуется установка пакета приложения.",
        ErrorCode.Cancelled => "Остановлено.",
        ErrorCode.SpeechFailure => "Не удалось воспроизвести речь.",
        ErrorCode.SpeechModelMissing => "Модель голоса не подготовлена. Откройте «Речь → Установка голосов».",
        ErrorCode.SpeechModelCorrupted => "Нарушена целостность модели голоса. В разделе «Речь → Установка голосов» подготовьте соответствующий набор повторно.",
        ErrorCode.InvalidSettings => "Настройки повреждены или имеют неподдерживаемую версию. Исходный файл сохранён.",
        _ => "Не удалось завершить операцию. Код: " + code
    };
}

public sealed record VoiceInfo(string Id, string Name, string Language);
public sealed record SpeechSegment(string Text, string Language, string VoiceId, int Speed);
public sealed record DetectedSegment(string Text, string? Language, bool HasLetters);
public sealed record PixelRegion(int X, int Y, int Width, int Height)
{
    public void Validate(int maximumDimension)
    {
        if (Width < 8 || Height < 8) throw new QuietException(ErrorCode.RegionTooSmall);
        if (Width > maximumDimension || Height > maximumDimension)
            throw new QuietException(ErrorCode.RegionTooLarge);
    }
    public static PixelRegion FromDrag(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
}

public static class TextLimits
{
    public const int Maximum = 60_000;
    public static string CombineSelection(IReadOnlyList<string> ranges)
    {
        var nonempty = ranges.Where(x => x.Length != 0).ToArray();
        long count = nonempty.Sum(x => (long)x.Length) + Math.Max(0, nonempty.Length - 1);
        if (count > Maximum) throw new QuietException(ErrorCode.TooLong);
        var result = string.Join("\n", nonempty);
        if (string.IsNullOrWhiteSpace(result)) throw new QuietException(ErrorCode.NoSelection);
        return result;
    }
}
