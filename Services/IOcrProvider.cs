using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public interface IOcrProvider
{
    Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken);
}
