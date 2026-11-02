#include <windows.h>
#include <objbase.h>
#include <shobjidl.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Media.SpeechSynthesis.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Management.Deployment.h>
#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.ApplicationModel.Activation.h>
#include <string>
#include <algorithm>
#include <cstring>
#include "SapiSpeech.h"

using namespace winrt;
using namespace Windows::Media::SpeechSynthesis;
using namespace Windows::Media::Ocr;
using namespace Windows::Storage::Streams;
using namespace Windows::Graphics::Imaging;

namespace {
struct Apartment {
    Apartment() { init_apartment(apartment_type::multi_threaded); }
    ~Apartment() { uninit_apartment(); }
};
void copy_string(std::wstring const& value, wchar_t** output) {
    auto bytes = (value.size() + 1) * sizeof(wchar_t);
    *output = static_cast<wchar_t*>(CoTaskMemAlloc(bytes));
    if (!*output) throw_hresult(E_OUTOFMEMORY);
    std::memcpy(*output, value.c_str(), bytes);
}
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_voices(wchar_t** result) noexcept {
    *result = nullptr;
    try {
        Apartment apartment;
        std::wstring output;
        for (auto const& voice : SpeechSynthesizer::AllVoices()) {
            output += voice.Id(); output += L'\t';
            output += voice.DisplayName(); output += L" [Windows]\t";
            output += voice.Language(); output += L'\n';
        }
        output += quiet_selection::sapi_voices();
        copy_string(output, result);
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_ocr_languages(wchar_t** result, uint32_t* max_dimension) noexcept {
    *result = nullptr;
    try {
        Apartment apartment;
        std::wstring output;
        for (auto const& language : OcrEngine::AvailableRecognizerLanguages()) {
            output += language.LanguageTag(); output += L'\n';
        }
        *max_dimension = OcrEngine::MaxImageDimension();
        copy_string(output, result);
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_synthesize(
    wchar_t const* voice_id, wchar_t const* text, double rate, uint8_t** data, uint32_t* length) noexcept {
    *data = nullptr; *length = 0;
    try {
        Apartment apartment;
        SpeechSynthesizer synth;
        bool found = false;
        for (auto const& voice : SpeechSynthesizer::AllVoices()) {
            if (voice.Id() == voice_id) { synth.Voice(voice); found = true; break; }
        }
        if (!found) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        synth.Options().SpeakingRate(rate);
        auto stream = synth.SynthesizeTextToStreamAsync(text).get();
        if (stream.Size() > 128 * 1024 * 1024) return E_OUTOFMEMORY;
        DataReader reader(stream.GetInputStreamAt(0));
        uint32_t size = static_cast<uint32_t>(stream.Size());
        if (reader.LoadAsync(size).get() != size) return E_FAIL;
        auto memory = static_cast<uint8_t*>(CoTaskMemAlloc(size));
        if (!memory) return E_OUTOFMEMORY;
        try { reader.ReadBytes(array_view<uint8_t>(memory, memory + size)); }
        catch (...) { CoTaskMemFree(memory); throw; }
        *data = memory; *length = size;
        reader.Close(); stream.Close(); synth.Close();
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_sapi_synthesize(
    wchar_t const* voice_id, wchar_t const* text, int speed, uint8_t** data, uint32_t* length) noexcept {
    *data = nullptr; *length = 0;
    try {
        Apartment apartment;
        quiet_selection::sapi_synthesize(voice_id, text, speed, data, length);
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_recognize(
    uint8_t const* pixels, int width, int height, wchar_t const* language_tag, wchar_t** result) noexcept {
    *result = nullptr;
    try {
        Apartment apartment;
        auto limit = OcrEngine::MaxImageDimension();
        if (width < 8 || height < 8 || static_cast<uint32_t>(width) > limit || static_cast<uint32_t>(height) > limit)
            return E_INVALIDARG;
        auto engine = OcrEngine::TryCreateFromLanguage(Windows::Globalization::Language(language_tag));
        if (!engine) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        DataWriter writer;
        auto size = static_cast<size_t>(width) * height * 4;
        writer.WriteBytes(array_view<uint8_t const>(pixels, pixels + size));
        auto bitmap = SoftwareBitmap::CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat::Bgra8,
            width, height, BitmapAlphaMode::Ignore);
        auto recognized = engine.RecognizeAsync(bitmap).get();
        std::wstring output;
        for (auto const& line : recognized.Lines()) {
            if (!output.empty()) output += L'\n';
            output += line.Text();
        }
        copy_string(output, result);
        bitmap.Close(); writer.Close();
        return S_OK;
    } catch (...) { return to_hresult(); }
}

// Like qs_recognize, but each line is "left top right bottom<TAB>text" in image pixels (the union
// of its word boxes), so the caller can pick the line under the pointer.
extern "C" __declspec(dllexport) HRESULT __cdecl qs_recognize_lines(
    uint8_t const* pixels, int width, int height, wchar_t const* language_tag, wchar_t** result) noexcept {
    *result = nullptr;
    try {
        Apartment apartment;
        auto limit = OcrEngine::MaxImageDimension();
        if (width < 8 || height < 8 || static_cast<uint32_t>(width) > limit || static_cast<uint32_t>(height) > limit)
            return E_INVALIDARG;
        auto engine = OcrEngine::TryCreateFromLanguage(Windows::Globalization::Language(language_tag));
        if (!engine) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        DataWriter writer;
        auto size = static_cast<size_t>(width) * height * 4;
        writer.WriteBytes(array_view<uint8_t const>(pixels, pixels + size));
        auto bitmap = SoftwareBitmap::CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat::Bgra8,
            width, height, BitmapAlphaMode::Ignore);
        auto recognized = engine.RecognizeAsync(bitmap).get();
        std::wstring output;
        for (auto const& line : recognized.Lines()) {
            float left = 1e9f, top = 1e9f, right = -1e9f, bottom = -1e9f;
            for (auto const& word : line.Words()) {
                auto box = word.BoundingRect();
                left = (std::min)(left, box.X); top = (std::min)(top, box.Y);
                right = (std::max)(right, box.X + box.Width); bottom = (std::max)(bottom, box.Y + box.Height);
            }
            if (right < left) continue;
            if (!output.empty()) output += L'\n';
            output += std::to_wstring(static_cast<int>(left)) + L' ' + std::to_wstring(static_cast<int>(top)) + L' '
                + std::to_wstring(static_cast<int>(right)) + L' ' + std::to_wstring(static_cast<int>(bottom)) + L'\t' + std::wstring(line.Text());
        }
        copy_string(output, result);
        bitmap.Close(); writer.Close();
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) void __cdecl qs_free(void* memory) noexcept { CoTaskMemFree(memory); }

// Package StartupTask "QuietSelectionStartup". enable: -1 read, 0 disable, 1 request enabling
// (Windows asks the user once). state receives the WinRT StartupTaskState value.
extern "C" __declspec(dllexport) HRESULT __cdecl qs_startup_task(int enable, int* state) noexcept {
    *state = 0;
    try {
        Apartment apartment;
        using Windows::ApplicationModel::StartupTaskState;
        auto task = Windows::ApplicationModel::StartupTask::GetAsync(L"QuietSelectionStartup").get();
        if (enable == 1 && task.State() == StartupTaskState::Disabled) task.RequestEnableAsync().get();
        else if (enable == 0 && task.State() == StartupTaskState::Enabled) task.Disable();
        *state = static_cast<int>(task.State());
        return S_OK;
    } catch (...) { return to_hresult(); }
}

// value = 1 when Windows launched the package at sign-in through its StartupTask.
extern "C" __declspec(dllexport) HRESULT __cdecl qs_activated_by_startup_task(int* value) noexcept {
    *value = 0;
    try {
        Apartment apartment;
        auto args = Windows::ApplicationModel::AppInstance::GetActivatedEventArgs();
        if (args && args.Kind() == Windows::ApplicationModel::Activation::ActivationKind::StartupTask) *value = 1;
        return S_OK;
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_install_package(wchar_t const* uri) noexcept {
    try {
        Apartment apartment;
        Windows::Management::Deployment::PackageManager manager;
        auto result = manager.AddPackageAsync(Windows::Foundation::Uri(uri), nullptr,
            Windows::Management::Deployment::DeploymentOptions::None).get();
        return result.ExtendedErrorCode();
    } catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __cdecl qs_activate_installed(wchar_t const* arguments, uint32_t* pid) noexcept {
    try {
        Apartment apartment;
        Windows::Management::Deployment::PackageManager manager;
        // Filter by our publisher/name in the OS query, never enumerate unrelated applications.
        for (auto const& package : manager.FindPackagesForUser(L"", L"QuietSelection.Windows", L"CN=QuietSelection.Local")) {
            auto activator = create_instance<IApplicationActivationManager>(CLSID_ApplicationActivationManager);
            std::wstring aumid = std::wstring(package.Id().FamilyName()) + L"!App";
            DWORD process_id = 0;
            auto hr = activator->ActivateApplication(aumid.c_str(), arguments, AO_NONE, &process_id);
            *pid = process_id;
            return hr;
        }
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    } catch (...) { return to_hresult(); }
}
