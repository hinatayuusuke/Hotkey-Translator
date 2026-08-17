#include <windows.h>
#include <objidl.h>
#include <wincodec.h>
#include <wincrypt.h>

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <utility>
#include <vector>

#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr wchar_t kDllName[] = L"oneocr.dll";
    constexpr wchar_t kModelName[] = L"oneocr.onemodel";
    constexpr wchar_t kOrtName[] = L"onnxruntime.dll";
    constexpr char kModelKey[] = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
    constexpr int kImageTypeBgra = 3;
    constexpr int kDefaultMaxLineCount = 1000;
    constexpr int kMinImageDimension = 50;
    constexpr int kMaxImageDimension = 10000;
    constexpr char kVersion[] = "1";

    struct ImageStructure
    {
        std::int32_t type;
        std::int32_t width;
        std::int32_t height;
        std::int32_t reserved;
        std::int64_t stepSize;
        std::uint8_t* dataPtr;
    };

    struct BoundingPolygon
    {
        float x1;
        float y1;
        float x2;
        float y2;
        float x3;
        float y3;
        float x4;
        float y4;
    };

    using CreateOcrInitOptionsFn = std::int64_t(__stdcall*)(std::int64_t*);
    using OcrInitOptionsSetUseModelDelayLoadFn = std::int64_t(__stdcall*)(std::int64_t, std::uint8_t);
    using CreateOcrPipelineFn = std::int64_t(__stdcall*)(const char*, const char*, std::int64_t, std::int64_t*);
    using CreateOcrProcessOptionsFn = std::int64_t(__stdcall*)(std::int64_t*);
    using OcrProcessOptionsSetMaxRecognitionLineCountFn = std::int64_t(__stdcall*)(std::int64_t, std::int64_t);
    using RunOcrPipelineFn = std::int64_t(__stdcall*)(std::int64_t, ImageStructure*, std::int64_t, std::int64_t*);
    using GetImageAngleFn = std::int64_t(__stdcall*)(std::int64_t, float*);
    using GetOcrLineCountFn = std::int64_t(__stdcall*)(std::int64_t, std::int64_t*);
    using GetOcrLineFn = std::int64_t(__stdcall*)(std::int64_t, std::int64_t, std::int64_t*);
    using GetOcrTextContentFn = std::int64_t(__stdcall*)(std::int64_t, char**);
    using GetOcrLineBoundingBoxFn = std::int64_t(__stdcall*)(std::int64_t, BoundingPolygon**);
    using GetOcrLineWordCountFn = std::int64_t(__stdcall*)(std::int64_t, std::int64_t*);
    using GetOcrWordFn = std::int64_t(__stdcall*)(std::int64_t, std::int64_t, std::int64_t*);
    using GetOcrWordBoundingBoxFn = std::int64_t(__stdcall*)(std::int64_t, BoundingPolygon**);
    using GetOcrWordConfidenceFn = std::int64_t(__stdcall*)(std::int64_t, float*);
    using ReleaseOcrResultFn = void(__stdcall*)(std::int64_t);
    using ReleaseOcrInitOptionsFn = void(__stdcall*)(std::int64_t);
    using ReleaseOcrPipelineFn = void(__stdcall*)(std::int64_t);
    using ReleaseOcrProcessOptionsFn = void(__stdcall*)(std::int64_t);

    struct DecodedImage
    {
        std::vector<std::uint8_t> pixels;
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        std::uint32_t stride = 0;
        std::uint32_t originalWidth = 0;
        std::uint32_t originalHeight = 0;
    };

    struct PolygonData
    {
        std::vector<std::pair<double, double>> points;
    };

    struct WordData
    {
        std::string text;
        double confidence = 1.0;
        PolygonData polygon;
    };

    struct LineData
    {
        std::string text;
        PolygonData polygon;
        std::vector<WordData> words;
    };

    struct OcrOutput
    {
        double durationMs = 0.0;
        double imageAngle = 0.0;
        std::uint32_t imageWidth = 0;
        std::uint32_t imageHeight = 0;
        std::vector<LineData> lines;
    };

    struct JsonBounds
    {
        double x = 0.0;
        double y = 0.0;
        double width = 0.0;
        double height = 0.0;
    };

    [[noreturn]] void Throw(const std::string& message)
    {
        throw std::runtime_error(message);
    }

    std::string WideToUtf8(const std::wstring& value)
    {
        if (value.empty())
        {
            return {};
        }

        const auto size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if (size <= 0)
        {
            Throw("WideCharToMultiByte failed.");
        }

        std::string result(size, '\0');
        if (WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr) != size)
        {
            Throw("WideCharToMultiByte wrote an unexpected size.");
        }

        return result;
    }

    std::wstring JoinPath(const std::wstring& left, const std::wstring& right)
    {
        if (left.empty())
        {
            return right;
        }

        if (left.back() == L'\\' || left.back() == L'/')
        {
            return left + right;
        }

        return left + L'\\' + right;
    }

    std::wstring ResolveAbsolutePath(const std::wstring& input)
    {
        std::vector<wchar_t> buffer(MAX_PATH);
        while (true)
        {
            const auto written = GetFullPathNameW(input.c_str(), static_cast<DWORD>(buffer.size()), buffer.data(), nullptr);
            if (written == 0)
            {
                Throw("GetFullPathNameW failed.");
            }

            if (written < buffer.size())
            {
                return std::wstring(buffer.data(), written);
            }

            buffer.resize(written + 1);
        }
    }

    std::string EscapeJson(std::string_view value)
    {
        std::ostringstream stream;
        for (const auto ch : value)
        {
            switch (ch)
            {
            case '\\':
                stream << "\\\\";
                break;
            case '"':
                stream << "\\\"";
                break;
            case '\b':
                stream << "\\b";
                break;
            case '\f':
                stream << "\\f";
                break;
            case '\n':
                stream << "\\n";
                break;
            case '\r':
                stream << "\\r";
                break;
            case '\t':
                stream << "\\t";
                break;
            default:
                if (static_cast<unsigned char>(ch) < 0x20)
                {
                    stream << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(static_cast<unsigned char>(ch)) << std::dec;
                }
                else
                {
                    stream << ch;
                }
                break;
            }
        }

        return stream.str();
    }

    bool TryFindJsonKey(std::string_view json, std::string_view key, std::size_t& valueStart)
    {
        const auto pattern = "\"" + std::string(key) + "\"";
        auto position = json.find(pattern);
        if (position == std::string_view::npos)
        {
            return false;
        }

        position = json.find(':', position + pattern.size());
        if (position == std::string_view::npos)
        {
            return false;
        }

        position += 1;
        while (position < json.size() && std::isspace(static_cast<unsigned char>(json[position])) != 0)
        {
            position += 1;
        }

        valueStart = position;
        return valueStart < json.size();
    }

    std::optional<std::string> TryReadJsonString(std::string_view json, std::string_view key)
    {
        std::size_t position = 0;
        if (!TryFindJsonKey(json, key, position) || json[position] != '"')
        {
            return std::nullopt;
        }

        position += 1;
        std::string result;
        result.reserve(64);

        while (position < json.size())
        {
            const auto ch = json[position++];
            if (ch == '"')
            {
                return result;
            }

            if (ch != '\\')
            {
                result.push_back(ch);
                continue;
            }

            if (position >= json.size())
            {
                break;
            }

            const auto escaped = json[position++];
            switch (escaped)
            {
            case '"':
            case '\\':
            case '/':
                result.push_back(escaped);
                break;
            case 'b':
                result.push_back('\b');
                break;
            case 'f':
                result.push_back('\f');
                break;
            case 'n':
                result.push_back('\n');
                break;
            case 'r':
                result.push_back('\r');
                break;
            case 't':
                result.push_back('\t');
                break;
            default:
                Throw("Unsupported JSON escape sequence.");
            }
        }

        Throw("Unterminated JSON string.");
    }

    std::optional<int> TryReadJsonInt(std::string_view json, std::string_view key)
    {
        std::size_t position = 0;
        if (!TryFindJsonKey(json, key, position))
        {
            return std::nullopt;
        }

        std::size_t end = position;
        if (json[end] == '-')
        {
            end += 1;
        }

        while (end < json.size() && std::isdigit(static_cast<unsigned char>(json[end])) != 0)
        {
            end += 1;
        }

        if (end == position)
        {
            return std::nullopt;
        }

        return std::stoi(std::string(json.substr(position, end - position)));
    }

    std::vector<std::uint8_t> DecodeBase64(const std::string& value)
    {
        DWORD required = 0;
        if (!CryptStringToBinaryA(value.c_str(), static_cast<DWORD>(value.size()), CRYPT_STRING_BASE64, nullptr, &required, nullptr, nullptr))
        {
            Throw("CryptStringToBinaryA size probe failed.");
        }

        std::vector<std::uint8_t> buffer(required);
        if (!CryptStringToBinaryA(
                value.c_str(),
                static_cast<DWORD>(value.size()),
                CRYPT_STRING_BASE64,
                buffer.data(),
                &required,
                nullptr,
                nullptr))
        {
            Throw("CryptStringToBinaryA decode failed.");
        }

        buffer.resize(required);
        return buffer;
    }

    DecodedImage DecodePngToBgra(IWICImagingFactory* factory, const std::vector<std::uint8_t>& imageBytes)
    {
        if (imageBytes.empty())
        {
            Throw("Image payload is empty.");
        }

        const auto memoryHandle = GlobalAlloc(GMEM_MOVEABLE, imageBytes.size());
        if (memoryHandle == nullptr)
        {
            Throw("GlobalAlloc failed.");
        }

        void* locked = GlobalLock(memoryHandle);
        if (locked == nullptr)
        {
            GlobalFree(memoryHandle);
            Throw("GlobalLock failed.");
        }

        std::memcpy(locked, imageBytes.data(), imageBytes.size());
        GlobalUnlock(memoryHandle);

        ComPtr<IStream> stream;
        HRESULT hr = CreateStreamOnHGlobal(memoryHandle, TRUE, &stream);
        if (FAILED(hr))
        {
            GlobalFree(memoryHandle);
            Throw("CreateStreamOnHGlobal failed.");
        }

        ComPtr<IWICBitmapDecoder> decoder;
        hr = factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, &decoder);
        if (FAILED(hr))
        {
            Throw("CreateDecoderFromStream failed.");
        }

        ComPtr<IWICBitmapFrameDecode> frame;
        hr = decoder->GetFrame(0, &frame);
        if (FAILED(hr))
        {
            Throw("GetFrame failed.");
        }

        UINT width = 0;
        UINT height = 0;
        hr = frame->GetSize(&width, &height);
        if (FAILED(hr))
        {
            Throw("GetSize failed.");
        }

        if (width > static_cast<UINT>(kMaxImageDimension) ||
            height > static_cast<UINT>(kMaxImageDimension))
        {
            Throw("Unsupported image size.");
        }

        ComPtr<IWICFormatConverter> converter;
        hr = factory->CreateFormatConverter(&converter);
        if (FAILED(hr))
        {
            Throw("CreateFormatConverter failed.");
        }

        hr = converter->Initialize(
            frame.Get(),
            GUID_WICPixelFormat32bppBGRA,
            WICBitmapDitherTypeNone,
            nullptr,
            0.0,
            WICBitmapPaletteTypeCustom);
        if (FAILED(hr))
        {
            Throw("WIC format conversion to BGRA failed.");
        }

        const auto sourceStride = width * 4;
        std::vector<std::uint8_t> sourcePixels(static_cast<std::size_t>(sourceStride) * height);
        hr = converter->CopyPixels(nullptr, sourceStride, static_cast<UINT>(sourcePixels.size()), sourcePixels.data());
        if (FAILED(hr))
        {
            Throw("CopyPixels failed.");
        }

        const auto paddedWidth = std::max<UINT>(width, kMinImageDimension);
        const auto paddedHeight = std::max<UINT>(height, kMinImageDimension);
        const auto paddedStride = paddedWidth * 4;

        DecodedImage image;
        image.originalWidth = width;
        image.originalHeight = height;
        image.width = paddedWidth;
        image.height = paddedHeight;
        image.stride = paddedStride;

        if (paddedWidth == width && paddedHeight == height)
        {
            image.pixels = std::move(sourcePixels);
        }
        else
        {
            // WHY: OneOCR rejects either dimension below 50 px. Padding only the right and
            // bottom keeps the source origin and every valid OCR coordinate unchanged.
            image.pixels.assign(static_cast<std::size_t>(paddedStride) * paddedHeight, 255);
            for (UINT row = 0; row < height; row += 1)
            {
                std::memcpy(
                    image.pixels.data() + static_cast<std::size_t>(row) * paddedStride,
                    sourcePixels.data() + static_cast<std::size_t>(row) * sourceStride,
                    sourceStride);
            }

            std::cerr << "Padded OneOCR image from " << width << 'x' << height
                      << " to " << paddedWidth << 'x' << paddedHeight << '.' << std::endl;
        }

        return image;
    }

    void ClipPolygon(PolygonData& polygon, std::uint32_t width, std::uint32_t height)
    {
        for (auto& [x, y] : polygon.points)
        {
            x = std::clamp(x, 0.0, static_cast<double>(width));
            y = std::clamp(y, 0.0, static_cast<double>(height));
        }
    }

    JsonBounds ComputeBounds(const PolygonData& polygon)
    {
        JsonBounds bounds;
        if (polygon.points.empty())
        {
            return bounds;
        }

        auto [minX, minY] = polygon.points.front();
        double maxX = minX;
        double maxY = minY;
        for (const auto& [x, y] : polygon.points)
        {
            minX = std::min(minX, x);
            minY = std::min(minY, y);
            maxX = std::max(maxX, x);
            maxY = std::max(maxY, y);
        }

        bounds.x = minX;
        bounds.y = minY;
        bounds.width = maxX - minX;
        bounds.height = maxY - minY;
        return bounds;
    }

    std::string SerializePolygon(const PolygonData& polygon)
    {
        std::ostringstream stream;
        stream << '[';
        for (std::size_t index = 0; index < polygon.points.size(); index += 1)
        {
            const auto& [x, y] = polygon.points[index];
            if (index > 0)
            {
                stream << ',';
            }

            stream << '[' << x << ',' << y << ']';
        }
        stream << ']';
        return stream.str();
    }

    std::string SerializeBounds(const JsonBounds& bounds)
    {
        std::ostringstream stream;
        stream << '[' << bounds.x << ',' << bounds.y << ',' << bounds.width << ',' << bounds.height << ']';
        return stream.str();
    }

    std::string SerializeWord(const WordData& word)
    {
        std::ostringstream stream;
        stream << "{\"text\":\"" << EscapeJson(word.text) << "\",\"confidence\":" << word.confidence
               << ",\"bbox\":" << SerializeBounds(ComputeBounds(word.polygon))
               << ",\"polygon\":" << SerializePolygon(word.polygon)
               << '}';
        return stream.str();
    }

    std::string SerializeLine(const LineData& line)
    {
        std::ostringstream stream;
        stream << "{\"text\":\"" << EscapeJson(line.text) << "\",\"bbox\":" << SerializeBounds(ComputeBounds(line.polygon))
               << ",\"polygon\":" << SerializePolygon(line.polygon)
               << ",\"words\":[";

        for (std::size_t index = 0; index < line.words.size(); index += 1)
        {
            if (index > 0)
            {
                stream << ',';
            }

            stream << SerializeWord(line.words[index]);
        }

        stream << "]}";
        return stream.str();
    }

    bool ReadExact(HANDLE pipe, void* buffer, DWORD byteCount)
    {
        auto* bytes = static_cast<std::uint8_t*>(buffer);
        DWORD total = 0;
        while (total < byteCount)
        {
            DWORD read = 0;
            if (!ReadFile(pipe, bytes + total, byteCount - total, &read, nullptr))
            {
                return false;
            }

            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    bool WriteExact(HANDLE pipe, const void* buffer, DWORD byteCount)
    {
        const auto* bytes = static_cast<const std::uint8_t*>(buffer);
        DWORD total = 0;
        while (total < byteCount)
        {
            DWORD written = 0;
            if (!WriteFile(pipe, bytes + total, byteCount - total, &written, nullptr))
            {
                return false;
            }

            if (written == 0)
            {
                return false;
            }

            total += written;
        }

        return true;
    }

    bool ReadMessage(HANDLE pipe, std::string& message)
    {
        std::uint32_t length = 0;
        if (!ReadExact(pipe, &length, sizeof(length)))
        {
            return false;
        }

        message.resize(length);
        if (length == 0)
        {
            return true;
        }

        return ReadExact(pipe, message.data(), length);
    }

    bool WriteMessage(HANDLE pipe, std::string_view message)
    {
        if (message.size() > std::numeric_limits<std::uint32_t>::max())
        {
            Throw("Message too large.");
        }

        const auto length = static_cast<std::uint32_t>(message.size());
        if (!WriteExact(pipe, &length, sizeof(length)))
        {
            return false;
        }

        if (length == 0)
        {
            return true;
        }

        return WriteExact(pipe, message.data(), length);
    }

    class OneOcrRuntime
    {
    public:
        void Initialize(const std::wstring& vendorDirectory, int maxLineCount)
        {
            vendorDirectory_ = ResolveAbsolutePath(vendorDirectory);

            const auto dllPath = JoinPath(vendorDirectory_, kDllName);
            const auto modelPath = JoinPath(vendorDirectory_, kModelName);
            const auto ortPath = JoinPath(vendorDirectory_, kOrtName);
            EnsureFileExists(dllPath);
            EnsureFileExists(modelPath);
            EnsureFileExists(ortPath);

            if (!SetDllDirectoryW(vendorDirectory_.c_str()))
            {
                Throw("SetDllDirectoryW failed.");
            }

            library_.reset(LoadLibraryW(dllPath.c_str()));
            if (!library_)
            {
                Throw("LoadLibraryW(oneocr.dll) failed.");
            }

            BindExports();
            Check(createInitOptions_(&initOptions_), "CreateOcrInitOptions");
            Check(setUseModelDelayLoad_(initOptions_, 0), "OcrInitOptionsSetUseModelDelayLoad");

            const auto modelUtf8 = WideToUtf8(modelPath);
            Check(createPipeline_(modelUtf8.c_str(), kModelKey, initOptions_, &pipeline_), "CreateOcrPipeline");
            Check(createProcessOptions_(&processOptions_), "CreateOcrProcessOptions");
            Check(setMaxRecognitionLineCount_(processOptions_, maxLineCount), "OcrProcessOptionsSetMaxRecognitionLineCount");
        }

        ~OneOcrRuntime()
        {
            if (releaseProcessOptions_ != nullptr && processOptions_ != 0)
            {
                releaseProcessOptions_(processOptions_);
            }

            if (releasePipeline_ != nullptr && pipeline_ != 0)
            {
                releasePipeline_(pipeline_);
            }

            if (releaseInitOptions_ != nullptr && initOptions_ != 0)
            {
                releaseInitOptions_(initOptions_);
            }
        }

        OcrOutput Recognize(const DecodedImage& image)
        {
            ImageStructure imageStructure{};
            imageStructure.type = kImageTypeBgra;
            imageStructure.width = static_cast<std::int32_t>(image.width);
            imageStructure.height = static_cast<std::int32_t>(image.height);
            imageStructure.reserved = 0;
            imageStructure.stepSize = image.stride;
            imageStructure.dataPtr = const_cast<std::uint8_t*>(image.pixels.data());

            std::int64_t resultHandle = 0;
            const auto startedAt = std::chrono::steady_clock::now();
            Check(runOcrPipeline_(pipeline_, &imageStructure, processOptions_, &resultHandle), "RunOcrPipeline");
            const auto duration = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - startedAt).count();

            struct ResultGuard
            {
                std::int64_t handle = 0;
                ReleaseOcrResultFn release = nullptr;

                ~ResultGuard()
                {
                    if (release != nullptr && handle != 0)
                    {
                        release(handle);
                    }
                }
            } guard{ resultHandle, releaseResult_ };

            OcrOutput output;
            output.durationMs = duration;
            output.imageWidth = image.originalWidth;
            output.imageHeight = image.originalHeight;

            float imageAngle = 0.0f;
            if (getImageAngle_(resultHandle, &imageAngle) == 0)
            {
                output.imageAngle = imageAngle;
            }

            std::int64_t lineCount = 0;
            Check(getOcrLineCount_(resultHandle, &lineCount), "GetOcrLineCount");
            output.lines.reserve(static_cast<std::size_t>(std::max<std::int64_t>(0, lineCount)));

            for (std::int64_t lineIndex = 0; lineIndex < lineCount; lineIndex += 1)
            {
                auto line = ReadLine(resultHandle, lineIndex);
                ClipPolygon(line.polygon, image.originalWidth, image.originalHeight);
                for (auto& word : line.words)
                {
                    ClipPolygon(word.polygon, image.originalWidth, image.originalHeight);
                }
                output.lines.push_back(std::move(line));
            }

            return output;
        }

    private:
        struct LibraryCloser
        {
            void operator()(HMODULE module) const noexcept
            {
                if (module != nullptr)
                {
                    FreeLibrary(module);
                }
            }
        };

        std::wstring vendorDirectory_;
        std::unique_ptr<std::remove_pointer_t<HMODULE>, LibraryCloser> library_;
        std::int64_t initOptions_ = 0;
        std::int64_t pipeline_ = 0;
        std::int64_t processOptions_ = 0;

        CreateOcrInitOptionsFn createInitOptions_ = nullptr;
        OcrInitOptionsSetUseModelDelayLoadFn setUseModelDelayLoad_ = nullptr;
        CreateOcrPipelineFn createPipeline_ = nullptr;
        CreateOcrProcessOptionsFn createProcessOptions_ = nullptr;
        OcrProcessOptionsSetMaxRecognitionLineCountFn setMaxRecognitionLineCount_ = nullptr;
        RunOcrPipelineFn runOcrPipeline_ = nullptr;
        GetImageAngleFn getImageAngle_ = nullptr;
        GetOcrLineCountFn getOcrLineCount_ = nullptr;
        GetOcrLineFn getOcrLine_ = nullptr;
        GetOcrTextContentFn getOcrLineContent_ = nullptr;
        GetOcrLineBoundingBoxFn getOcrLineBoundingBox_ = nullptr;
        GetOcrLineWordCountFn getOcrLineWordCount_ = nullptr;
        GetOcrWordFn getOcrWord_ = nullptr;
        GetOcrTextContentFn getOcrWordContent_ = nullptr;
        GetOcrWordBoundingBoxFn getOcrWordBoundingBox_ = nullptr;
        GetOcrWordConfidenceFn getOcrWordConfidence_ = nullptr;
        ReleaseOcrResultFn releaseResult_ = nullptr;
        ReleaseOcrInitOptionsFn releaseInitOptions_ = nullptr;
        ReleaseOcrPipelineFn releasePipeline_ = nullptr;
        ReleaseOcrProcessOptionsFn releaseProcessOptions_ = nullptr;

        static void EnsureFileExists(const std::wstring& path)
        {
            const auto attributes = GetFileAttributesW(path.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                Throw("Required OneOCR vendor file is missing.");
            }
        }

        template <typename T>
        T Bind(const char* name)
        {
            const auto address = GetProcAddress(library_.get(), name);
            if (address == nullptr)
            {
                Throw(std::string("Missing DLL export: ") + name);
            }

            return reinterpret_cast<T>(address);
        }

        void BindExports()
        {
            createInitOptions_ = Bind<CreateOcrInitOptionsFn>("CreateOcrInitOptions");
            setUseModelDelayLoad_ = Bind<OcrInitOptionsSetUseModelDelayLoadFn>("OcrInitOptionsSetUseModelDelayLoad");
            createPipeline_ = Bind<CreateOcrPipelineFn>("CreateOcrPipeline");
            createProcessOptions_ = Bind<CreateOcrProcessOptionsFn>("CreateOcrProcessOptions");
            setMaxRecognitionLineCount_ = Bind<OcrProcessOptionsSetMaxRecognitionLineCountFn>("OcrProcessOptionsSetMaxRecognitionLineCount");
            runOcrPipeline_ = Bind<RunOcrPipelineFn>("RunOcrPipeline");
            getImageAngle_ = Bind<GetImageAngleFn>("GetImageAngle");
            getOcrLineCount_ = Bind<GetOcrLineCountFn>("GetOcrLineCount");
            getOcrLine_ = Bind<GetOcrLineFn>("GetOcrLine");
            getOcrLineContent_ = Bind<GetOcrTextContentFn>("GetOcrLineContent");
            getOcrLineBoundingBox_ = Bind<GetOcrLineBoundingBoxFn>("GetOcrLineBoundingBox");
            getOcrLineWordCount_ = Bind<GetOcrLineWordCountFn>("GetOcrLineWordCount");
            getOcrWord_ = Bind<GetOcrWordFn>("GetOcrWord");
            getOcrWordContent_ = Bind<GetOcrTextContentFn>("GetOcrWordContent");
            getOcrWordBoundingBox_ = Bind<GetOcrWordBoundingBoxFn>("GetOcrWordBoundingBox");
            getOcrWordConfidence_ = Bind<GetOcrWordConfidenceFn>("GetOcrWordConfidence");
            releaseResult_ = Bind<ReleaseOcrResultFn>("ReleaseOcrResult");
            releaseInitOptions_ = Bind<ReleaseOcrInitOptionsFn>("ReleaseOcrInitOptions");
            releasePipeline_ = Bind<ReleaseOcrPipelineFn>("ReleaseOcrPipeline");
            releaseProcessOptions_ = Bind<ReleaseOcrProcessOptionsFn>("ReleaseOcrProcessOptions");
        }

        static void Check(std::int64_t resultCode, const char* operation)
        {
            if (resultCode != 0)
            {
                Throw(std::string(operation) + " failed with code " + std::to_string(resultCode) + ".");
            }
        }

        static PolygonData ReadPolygon(BoundingPolygon* polygon)
        {
            if (polygon == nullptr)
            {
                Throw("Bounding polygon is null.");
            }

            PolygonData result;
            result.points.reserve(4);
            result.points.emplace_back(polygon->x1, polygon->y1);
            result.points.emplace_back(polygon->x2, polygon->y2);
            result.points.emplace_back(polygon->x3, polygon->y3);
            result.points.emplace_back(polygon->x4, polygon->y4);
            return result;
        }

        std::string ReadText(std::int64_t handle, GetOcrTextContentFn accessor)
        {
            char* content = nullptr;
            Check(accessor(handle, &content), "GetText");
            return content == nullptr ? std::string() : std::string(content);
        }

        LineData ReadLine(std::int64_t resultHandle, std::int64_t lineIndex)
        {
            std::int64_t lineHandle = 0;
            Check(getOcrLine_(resultHandle, lineIndex, &lineHandle), "GetOcrLine");

            BoundingPolygon* polygon = nullptr;
            Check(getOcrLineBoundingBox_(lineHandle, &polygon), "GetOcrLineBoundingBox");

            std::int64_t wordCount = 0;
            Check(getOcrLineWordCount_(lineHandle, &wordCount), "GetOcrLineWordCount");

            LineData line;
            line.text = ReadText(lineHandle, getOcrLineContent_);
            line.polygon = ReadPolygon(polygon);
            line.words.reserve(static_cast<std::size_t>(std::max<std::int64_t>(0, wordCount)));

            for (std::int64_t wordIndex = 0; wordIndex < wordCount; wordIndex += 1)
            {
                line.words.push_back(ReadWord(lineHandle, wordIndex));
            }

            return line;
        }

        WordData ReadWord(std::int64_t lineHandle, std::int64_t wordIndex)
        {
            std::int64_t wordHandle = 0;
            Check(getOcrWord_(lineHandle, wordIndex, &wordHandle), "GetOcrWord");

            BoundingPolygon* polygon = nullptr;
            Check(getOcrWordBoundingBox_(wordHandle, &polygon), "GetOcrWordBoundingBox");

            WordData word;
            word.text = ReadText(wordHandle, getOcrWordContent_);
            word.polygon = ReadPolygon(polygon);

            float confidence = 1.0f;
            if (getOcrWordConfidence_(wordHandle, &confidence) == 0)
            {
                word.confidence = confidence;
            }

            return word;
        }
    };

    struct HelperArguments
    {
        std::wstring pipeName;
        std::wstring vendorDirectory;
        int maxLineCount = kDefaultMaxLineCount;
    };

    HelperArguments ParseArguments(int argc, wchar_t** argv)
    {
        HelperArguments arguments;
        for (int index = 1; index < argc; index += 1)
        {
            const std::wstring current = argv[index];
            if (current == L"--pipe" && index + 1 < argc)
            {
                arguments.pipeName = argv[++index];
            }
            else if (current == L"--vendor-dir" && index + 1 < argc)
            {
                arguments.vendorDirectory = argv[++index];
            }
            else if (current == L"--max-line-count" && index + 1 < argc)
            {
                arguments.maxLineCount = _wtoi(argv[++index]);
            }
            else
            {
                Throw("Unsupported command-line argument.");
            }
        }

        if (arguments.pipeName.empty() || arguments.vendorDirectory.empty())
        {
            Throw("Missing required arguments: --pipe and --vendor-dir.");
        }

        if (arguments.maxLineCount <= 0)
        {
            arguments.maxLineCount = kDefaultMaxLineCount;
        }

        return arguments;
    }

    std::string BuildReadyJson()
    {
        std::ostringstream stream;
        stream << "{\"type\":\"ready\",\"ok\":true,\"version\":\"" << kVersion << "\",\"message\":\"OneOCR helper ready\"}";
        return stream.str();
    }

    std::string BuildOkJson(const std::string& id, std::string_view type)
    {
        std::ostringstream stream;
        stream << "{\"id\":\"" << EscapeJson(id) << "\",\"type\":\"" << type << "\",\"ok\":true}";
        return stream.str();
    }

    std::string BuildErrorJson(const std::string& id, std::string_view type, const std::string& error)
    {
        std::ostringstream stream;
        stream << "{\"id\":\"" << EscapeJson(id) << "\",\"type\":\"" << type << "\",\"ok\":false,\"error\":\"" << EscapeJson(error) << "\"}";
        return stream.str();
    }

    std::string BuildRecognizeJson(const std::string& id, const OcrOutput& output)
    {
        std::ostringstream stream;
        stream << "{\"id\":\"" << EscapeJson(id) << "\",\"type\":\"recognizeResult\",\"ok\":true"
               << ",\"durationMs\":" << output.durationMs
               << ",\"imageWidth\":" << output.imageWidth
               << ",\"imageHeight\":" << output.imageHeight
               << ",\"imageAngle\":" << output.imageAngle
               << ",\"lines\":[";

        for (std::size_t index = 0; index < output.lines.size(); index += 1)
        {
            if (index > 0)
            {
                stream << ',';
            }

            stream << SerializeLine(output.lines[index]);
        }

        stream << "]}";
        return stream.str();
    }

    int Run(int argc, wchar_t** argv)
    {
        const auto arguments = ParseArguments(argc, argv);
        const auto fullPipeName = L"\\\\.\\pipe\\" + arguments.pipeName;

        const auto hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(hr))
        {
            Throw("CoInitializeEx failed.");
        }

        struct CoUninitializeGuard
        {
            ~CoUninitializeGuard()
            {
                CoUninitialize();
            }
        } coGuard;

        ComPtr<IWICImagingFactory> imagingFactory;
        if (FAILED(CoCreateInstance(
                CLSID_WICImagingFactory,
                nullptr,
                CLSCTX_INPROC_SERVER,
                IID_PPV_ARGS(&imagingFactory))))
        {
            Throw("Failed to create WIC imaging factory.");
        }

        OneOcrRuntime runtime;
        runtime.Initialize(arguments.vendorDirectory, arguments.maxLineCount);

        HANDLE pipe = CreateNamedPipeW(
            fullPipeName.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            8 * 1024 * 1024,
            8 * 1024 * 1024,
            0,
            nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            Throw("CreateNamedPipeW failed.");
        }

        struct HandleCloser
        {
            HANDLE handle = INVALID_HANDLE_VALUE;

            ~HandleCloser()
            {
                if (handle != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(handle);
                }
            }
        } pipeGuard{ pipe };

        if (!ConnectNamedPipe(pipe, nullptr))
        {
            const auto error = GetLastError();
            if (error != ERROR_PIPE_CONNECTED)
            {
                Throw("ConnectNamedPipe failed.");
            }
        }

        if (!WriteMessage(pipe, BuildReadyJson()))
        {
            Throw("Failed to send ready packet.");
        }

        while (true)
        {
            std::string requestJson;
            if (!ReadMessage(pipe, requestJson))
            {
                break;
            }

            const auto requestId = TryReadJsonString(requestJson, "id").value_or("unknown");

            try
            {
                const auto requestType = TryReadJsonString(requestJson, "type").value_or("");
                if (requestType == "ping")
                {
                    if (!WriteMessage(pipe, BuildOkJson(requestId, "pong")))
                    {
                        break;
                    }

                    continue;
                }

                if (requestType == "version")
                {
                    std::ostringstream response;
                    response << "{\"id\":\"" << EscapeJson(requestId) << "\",\"type\":\"version\",\"ok\":true,\"version\":\"" << kVersion << "\"}";
                    if (!WriteMessage(pipe, response.str()))
                    {
                        break;
                    }

                    continue;
                }

                if (requestType == "shutdown")
                {
                    WriteMessage(pipe, BuildOkJson(requestId, "shutdown"));
                    break;
                }

                if (requestType != "recognize")
                {
                    Throw("Unsupported request type.");
                }

                const auto base64 = TryReadJsonString(requestJson, "imageBytesBase64");
                if (!base64.has_value() || base64->empty())
                {
                    Throw("imageBytesBase64 is required.");
                }

                const auto imageBytes = DecodeBase64(*base64);
                const auto image = DecodePngToBgra(imagingFactory.Get(), imageBytes);
                const auto output = runtime.Recognize(image);
                if (!WriteMessage(pipe, BuildRecognizeJson(requestId, output)))
                {
                    break;
                }
            }
            catch (const std::exception& ex)
            {
                if (!WriteMessage(pipe, BuildErrorJson(requestId, "error", ex.what())))
                {
                    break;
                }
            }
        }

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        return 0;
    }
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        return Run(argc, argv);
    }
    catch (const std::exception& ex)
    {
        std::cerr << ex.what() << std::endl;
        return 1;
    }
}
