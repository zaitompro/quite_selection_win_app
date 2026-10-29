#include "SapiSpeech.h"
#include <sapi.h>
#include <winrt/base.h>
#include <algorithm>
#include <cstring>
#include <vector>

using namespace winrt;
namespace quiet_selection {
namespace {
constexpr size_t maximum_audio = 128 * 1024 * 1024;

// SAPI writes PCM into a bounded COM stream. No temporary file or device is opened.
struct AudioStream : implements<AudioStream, IStream> {
    std::vector<uint8_t> bytes;
    size_t position = 0;
    HRESULT __stdcall Read(void* target, ULONG count, ULONG* read) noexcept override {
        auto actual = static_cast<ULONG>(std::min<size_t>(count, bytes.size() - std::min(position, bytes.size())));
        if (actual) std::memcpy(target, bytes.data() + position, actual);
        position += actual;
        if (read) *read = actual;
        return actual == count ? S_OK : S_FALSE;
    }
    HRESULT __stdcall Write(void const* source, ULONG count, ULONG* written) noexcept override {
        if (written) *written = 0;
        if (count > maximum_audio - position) return STG_E_MEDIUMFULL;
        try {
            if (position + count > bytes.size()) bytes.resize(position + count);
            if (count) std::memcpy(bytes.data() + position, source, count);
            position += count;
            if (written) *written = count;
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT __stdcall Seek(LARGE_INTEGER offset, DWORD origin, ULARGE_INTEGER* result) noexcept override {
        if (origin > STREAM_SEEK_END) return STG_E_INVALIDFUNCTION;
        auto base = origin == STREAM_SEEK_SET ? 0LL : static_cast<LONGLONG>(origin == STREAM_SEEK_CUR ? position : bytes.size());
        if (offset.QuadPart < -base || offset.QuadPart > static_cast<LONGLONG>(maximum_audio) - base) return STG_E_INVALIDFUNCTION;
        position = static_cast<size_t>(base + offset.QuadPart);
        if (result) result->QuadPart = position;
        return S_OK;
    }
    HRESULT __stdcall SetSize(ULARGE_INTEGER size) noexcept override {
        if (size.QuadPart > maximum_audio) return STG_E_MEDIUMFULL;
        try { bytes.resize(static_cast<size_t>(size.QuadPart)); return S_OK; }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT __stdcall CopyTo(IStream*, ULARGE_INTEGER, ULARGE_INTEGER*, ULARGE_INTEGER*) noexcept override { return E_NOTIMPL; }
    HRESULT __stdcall Commit(DWORD) noexcept override { return S_OK; }
    HRESULT __stdcall Revert() noexcept override { return E_NOTIMPL; }
    HRESULT __stdcall LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) noexcept override { return STG_E_INVALIDFUNCTION; }
    HRESULT __stdcall UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) noexcept override { return STG_E_INVALIDFUNCTION; }
    HRESULT __stdcall Stat(STATSTG* info, DWORD) noexcept override {
        if (!info) return STG_E_INVALIDPOINTER;
        *info = {}; info->type = STGTY_STREAM; info->cbSize.QuadPart = bytes.size(); return S_OK;
    }
    HRESULT __stdcall Clone(IStream**) noexcept override { return E_NOTIMPL; }
};

com_ptr<IEnumSpObjectTokens> tokens() {
    auto category = create_instance<ISpObjectTokenCategory>(CLSID_SpObjectTokenCategory);
    check_hresult(category->SetId(SPCAT_VOICES, FALSE));
    com_ptr<IEnumSpObjectTokens> result;
    check_hresult(category->EnumTokens(nullptr, nullptr, result.put()));
    return result;
}
std::wstring token_id(ISpObjectToken* token) {
    wchar_t* id = nullptr;
    check_hresult(token->GetId(&id));
    std::wstring result(id); CoTaskMemFree(id); return result;
}
std::wstring value(ISpDataKey* key, wchar_t const* name) {
    wchar_t* text = nullptr;
    if (FAILED(key->GetStringValue(name, &text))) return {};
    std::wstring result(text); CoTaskMemFree(text);
    for (auto& c : result) if (c == L'\t' || c == L'\r' || c == L'\n') c = L' ';
    return result;
}
}

std::wstring sapi_voices() {
    std::wstring output;
    auto all = tokens();
    com_ptr<ISpObjectToken> token;
    while (all->Next(1, token.put(), nullptr) == S_OK) {
        com_ptr<ISpDataKey> attributes;
        if (SUCCEEDED(token->OpenKey(L"Attributes", attributes.put()))) {
            auto language = value(attributes.get(), L"Language");
            wchar_t* end = nullptr;
            auto locale_id = wcstoul(language.c_str(), &end, 16);
            wchar_t locale[LOCALE_NAME_MAX_LENGTH]{};
            if (end != language.c_str() && LCIDToLocaleName(locale_id, locale, LOCALE_NAME_MAX_LENGTH, 0)) {
                auto name = value(token.get(), nullptr);
                auto id = token_id(token.get());
                if (id.find(L"\\TokenEnums\\RHVoice\\") != std::wstring::npos) name = L"RHVoice " + name;
                output += L"sapi5:" + id + L"\t" + name + L" [SAPI 5]\t" + locale + L"\n";
            }
        }
        token = nullptr;
    }
    return output;
}

void sapi_synthesize(wchar_t const* id, wchar_t const* text, int speed, uint8_t** data, uint32_t* length) {
    if (speed < 0 || speed > 100) throw_hresult(E_INVALIDARG);
    // Only resolve a token from the current enumeration, never an arbitrary registry path.
    auto all = tokens();
    com_ptr<ISpObjectToken> selected, token;
    while (all->Next(1, token.put(), nullptr) == S_OK) {
        if (token_id(token.get()) == id) { selected = token; break; }
        token = nullptr;
    }
    if (!selected) throw_hresult(HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
    auto voice = create_instance<ISpVoice>(CLSID_SpVoice);
    check_hresult(voice->SetVoice(selected.get()));
    check_hresult(voice->SetRate((speed - 50) / 5));
    auto memory = make_self<AudioStream>();
    auto stream = create_instance<ISpStream>(CLSID_SpStream);
    WAVEFORMATEX format{ WAVE_FORMAT_PCM, 1, 24000, 48000, 2, 16, 0 };
    check_hresult(stream->SetBaseStream(memory.get(), SPDFID_WaveFormatEx, &format));
    check_hresult(voice->SetOutput(stream.get(), FALSE));
    // Explicitly disable XML: selected text must never turn into SAPI file/bookmark commands.
    check_hresult(voice->Speak(text, SPF_IS_NOT_XML, nullptr));
    check_hresult(stream->Commit(STGC_DEFAULT));
    auto size = static_cast<uint32_t>(memory->bytes.size());
    if (!size || size % format.nBlockAlign) throw_hresult(E_FAIL);
    auto wav = static_cast<uint8_t*>(CoTaskMemAlloc(size + 44));
    if (!wav) throw_hresult(E_OUTOFMEMORY);
    std::memcpy(wav, "RIFF", 4);
    auto riff_size = size + 36; std::memcpy(wav + 4, &riff_size, 4);
    std::memcpy(wav + 8, "WAVEfmt ", 8);
    uint32_t format_size = 16; std::memcpy(wav + 16, &format_size, 4);
    std::memcpy(wav + 20, &format, 16);
    std::memcpy(wav + 36, "data", 4); std::memcpy(wav + 40, &size, 4);
    std::memcpy(wav + 44, memory->bytes.data(), size);
    *data = wav; *length = size + 44;
}
}
