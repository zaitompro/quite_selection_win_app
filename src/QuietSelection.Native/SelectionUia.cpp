// Reads the selected text of the foreground window through the native UI Automation client.
// Search order: the focused element and up to six parents (an empty selection does not stop the
// climb), then a bounded look into the children of the focused element. Never reads password
// fields or elements of other processes. The diagnostic string holds only control types, class
// names and step results, never names, values or text.
#include <windows.h>
#include <ole2.h>
#include <UIAutomation.h>
#include <winrt/base.h>
#include <algorithm>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <new>
#include <string>
#include <vector>

namespace {
enum Status : int { Ok, NoSelection, Unavailable, NoFocus, Denied, Protected, TooLong, SourceChanged, ProviderFailure };
constexpr int MaximumParentSteps = 6;
constexpr int MaximumChildrenPerLevel = 40;
constexpr size_t MaximumDiagnostic = 600;

struct Bstr {
    BSTR value = nullptr;
    ~Bstr() { SysFreeString(value); }
    BSTR* put() { SysFreeString(value); value = nullptr; return &value; }
    size_t size() const { return value ? SysStringLen(value) : 0; }
};

struct TooLongError {};

void copy_out(std::wstring const& value, wchar_t** output) {
    auto bytes = (value.size() + 1) * sizeof(wchar_t);
    *output = static_cast<wchar_t*>(CoTaskMemAlloc(bytes));
    if (!*output) throw std::bad_alloc();
    std::memcpy(*output, value.c_str(), bytes);
}

winrt::com_ptr<IUIAutomation> automation() {
    thread_local winrt::com_ptr<IUIAutomation> instance;
    if (!instance) {
        winrt::com_ptr<IUIAutomation> created;
        if (FAILED(CoCreateInstance(__uuidof(CUIAutomation8), nullptr, CLSCTX_INPROC_SERVER, __uuidof(IUIAutomation), created.put_void())))
            winrt::check_hresult(CoCreateInstance(__uuidof(CUIAutomation), nullptr, CLSCTX_INPROC_SERVER, __uuidof(IUIAutomation), created.put_void()));
        instance = created;
    }
    return instance;
}

// The source window's process plus processes of its child windows (UWP apps, embedded hosts).
struct Processes {
    std::vector<DWORD> ids;
    bool contains(DWORD id) const { return std::find(ids.begin(), ids.end(), id) != ids.end(); }
};

BOOL CALLBACK add_child_process(HWND child, LPARAM state) {
    auto& processes = *reinterpret_cast<Processes*>(state);
    DWORD id = 0;
    GetWindowThreadProcessId(child, &id);
    if (id && !processes.contains(id)) processes.ids.push_back(id);
    return processes.ids.size() < 16;
}

class Reader {
public:
    Reader(HWND source, uint32_t maximum) : source_(source), maximum_(maximum) {}
    std::wstring diagnostic;
    std::wstring text;

    Status Run() {
        if (GetForegroundWindow() != source_) return SourceChanged;
        DWORD sourceProcess = 0;
        GetWindowThreadProcessId(source_, &sourceProcess);
        processes_.ids.push_back(sourceProcess);
        EnumChildWindows(source_, add_child_process, reinterpret_cast<LPARAM>(&processes_));

        uia_ = automation();
        winrt::check_hresult(uia_->get_ControlViewWalker(walker_.put()));
        winrt::com_ptr<IUIAutomationElement> focused;
        if (FAILED(uia_->GetFocusedElement(focused.put())) || !focused) {
            note(L"focus=none;");
            winrt::check_hresult(uia_->ElementFromHandle(source_, focused.put()));
            if (!focused) return NoFocus;
        }
        if (!processes_.contains(process_of(focused.get()))) { note(L"focus=other-process;"); return NoFocus; }

        bool sawEmpty = false;
        winrt::com_ptr<IUIAutomationElement> current = focused;
        for (int depth = 0; depth <= MaximumParentSteps && current; depth++) {
            if (!processes_.contains(process_of(current.get()))) { note(L"up:stop-other-process;"); break; }
            if (is_password(current.get())) return Protected;
            note(L"up" + std::to_wstring(depth) + L":" + describe(current.get()));
            switch (read_selection(current.get())) {
                case 1: return finish();
                case 0: sawEmpty = true; break;
                default: break;
            }
            winrt::com_ptr<IUIAutomationElement> parent;
            if (FAILED(walker_->GetParentElement(current.get(), parent.put()))) break;
            current = parent;
        }

        // Viewers whose focus stays on a frame or pane while the document is a child.
        std::vector<winrt::com_ptr<IUIAutomationElement>> level{ focused };
        for (int depth = 1; depth <= 2 && !level.empty(); depth++) {
            std::vector<winrt::com_ptr<IUIAutomationElement>> next;
            for (auto const& parent : level) {
                winrt::com_ptr<IUIAutomationElement> child;
                if (FAILED(walker_->GetFirstChildElement(parent.get(), child.put()))) continue;
                for (int count = 0; child && count < MaximumChildrenPerLevel; count++) {
                    if (processes_.contains(process_of(child.get())) && !is_password(child.get()) && has_text_pattern(child.get())) {
                        note(L"down" + std::to_wstring(depth) + L":" + describe(child.get()));
                        int found = read_selection(child.get());
                        if (found == 1) return finish();
                        if (found == 0) sawEmpty = true;
                    }
                    next.push_back(child);
                    winrt::com_ptr<IUIAutomationElement> sibling;
                    if (FAILED(walker_->GetNextSiblingElement(child.get(), sibling.put()))) break;
                    child = sibling;
                }
            }
            level = std::move(next);
        }
        if (GetForegroundWindow() != source_) return SourceChanged;
        return sawEmpty ? NoSelection : Unavailable;
    }

public: // Helpers are shared with PointReader below.
    HWND source_;
    uint32_t maximum_;
    Processes processes_;
    winrt::com_ptr<IUIAutomation> uia_;
    winrt::com_ptr<IUIAutomationTreeWalker> walker_;

    Status finish() { return GetForegroundWindow() == source_ ? Ok : SourceChanged; }

    void note(std::wstring const& part) {
        if (diagnostic.size() + part.size() <= MaximumDiagnostic) diagnostic += part;
    }

    static DWORD process_of(IUIAutomationElement* element) {
        int id = 0;
        return SUCCEEDED(element->get_CurrentProcessId(&id)) ? static_cast<DWORD>(id) : 0;
    }

    static bool is_password(IUIAutomationElement* element) {
        BOOL value = FALSE;
        return SUCCEEDED(element->get_CurrentIsPassword(&value)) && value;
    }

    static bool has_text_pattern(IUIAutomationElement* element) {
        VARIANT value; VariantInit(&value);
        bool available = SUCCEEDED(element->GetCurrentPropertyValue(UIA_IsTextPatternAvailablePropertyId, &value))
            && value.vt == VT_BOOL && value.boolVal == VARIANT_TRUE;
        VariantClear(&value);
        return available;
    }

    // Control type and class name only; these come from the program, not from its content.
    static std::wstring describe(IUIAutomationElement* element) {
        CONTROLTYPEID type = 0;
        element->get_CurrentControlType(&type);
        std::wstring name;
        switch (type) {
            case UIA_EditControlTypeId: name = L"Edit"; break;
            case UIA_DocumentControlTypeId: name = L"Document"; break;
            case UIA_TextControlTypeId: name = L"Text"; break;
            case UIA_PaneControlTypeId: name = L"Pane"; break;
            case UIA_WindowControlTypeId: name = L"Window"; break;
            case UIA_GroupControlTypeId: name = L"Group"; break;
            case UIA_CustomControlTypeId: name = L"Custom"; break;
            case UIA_ListItemControlTypeId: name = L"ListItem"; break;
            case UIA_DataItemControlTypeId: name = L"DataItem"; break;
            default: name = std::to_wstring(type); break;
        }
        Bstr className;
        if (SUCCEEDED(element->get_CurrentClassName(className.put())) && className.size() > 0)
            name += L"[" + std::wstring(className.value, std::min<size_t>(className.size(), 40)) + L"]";
        return name;
    }

    // 1: text found (kept in text); 0: supports selection but it is empty; -1: no selection here.
    int read_selection(IUIAutomationElement* element) {
        winrt::com_ptr<IUIAutomationTextPattern> pattern;
        if (FAILED(element->GetCurrentPatternAs(UIA_TextPatternId, __uuidof(IUIAutomationTextPattern), pattern.put_void())) || !pattern) {
            note(L" -;");
            return -1;
        }
        SupportedTextSelection support = SupportedTextSelection_Single;
        if (SUCCEEDED(pattern->get_SupportedTextSelection(&support)) && support == SupportedTextSelection_None) {
            note(L" text,no-selection;");
            return -1;
        }
        winrt::com_ptr<IUIAutomationTextRangeArray> array;
        HRESULT hr = pattern->GetSelection(array.put());
        if (FAILED(hr) || !array) { note(L" selection-failed;"); if (hr == E_ACCESSDENIED) winrt::throw_hresult(hr); return -1; }
        int count = 0;
        array->get_Length(&count);
        std::vector<winrt::com_ptr<IUIAutomationTextRange>> ranges;
        for (int i = 0; i < count; i++) {
            winrt::com_ptr<IUIAutomationTextRange> range;
            if (FAILED(array->GetElement(i, range.put())) || !range) continue;
            int compare = 0;
            if (SUCCEEDED(range->CompareEndpoints(TextPatternRangeEndpoint_Start, range.get(), TextPatternRangeEndpoint_End, &compare)) && compare == 0) continue;
            ranges.push_back(range);
        }
        std::sort(ranges.begin(), ranges.end(), [](auto const& left, auto const& right) {
            int compare = 0;
            left->CompareEndpoints(TextPatternRangeEndpoint_Start, right.get(), TextPatternRangeEndpoint_Start, &compare);
            return compare < 0;
        });
        std::wstring joined;
        for (auto const& range : ranges) {
            size_t used = joined.size() + (joined.empty() ? 0 : 1);
            if (used >= maximum_) throw TooLongError{};
            int remaining = static_cast<int>(maximum_ - used);
            Bstr part;
            if (FAILED(range->GetText(remaining + 1, part.put()))) continue;
            if (part.size() > static_cast<size_t>(remaining)) throw TooLongError{};
            if (part.size() == 0) continue;
            if (!joined.empty()) joined += L'\n';
            joined.append(part.value, part.size());
        }
        bool blank = std::all_of(joined.begin(), joined.end(), [](wchar_t c) { return std::iswspace(c) != 0; });
        note(blank ? L" text,empty;" : L" text,selected;");
        if (blank) return 0;
        text = std::move(joined);
        return 1;
    }
};

// Whitespace, control characters and the object replacement character (images, canvases) carry no
// readable text; such a "paragraph" falls through to the name or to recognition.
bool is_blank(std::wstring const& value) {
    return std::all_of(value.begin(), value.end(), [](wchar_t c) {
        return std::iswspace(c) != 0 || c < 0x20 || c == 0xFFFC || c == 0x200B || c == 0xFEFF;
    });
}

// Text under a screen point: the paragraph (or line) of a text control, otherwise the name of the
// element there (button, label, menu item, list item). Window, pane and document titles are not read.
class PointReader {
public:
    PointReader(POINT point, uint32_t maximum, DWORD excluded) : point_(point), maximum_(maximum), excluded_(excluded) {}
    std::wstring diagnostic;
    std::wstring text;

    Status Run() {
        auto uia = automation();
        winrt::check_hresult(uia->get_ControlViewWalker(walker_.put()));
        winrt::com_ptr<IUIAutomationElement> hit;
        winrt::check_hresult(uia->ElementFromPoint(point_, hit.put()));
        if (!hit) return Unavailable;
        DWORD process = Reader::process_of(hit.get());
        if (process == excluded_ || process == GetCurrentProcessId()) { note(L"hit=own-window;"); return Unavailable; }
        if (Reader::is_password(hit.get())) return Protected;
        note(L"hit:" + Reader::describe(hit.get()) + L";");

        // A text control at or above the element: the paragraph under the point.
        winrt::com_ptr<IUIAutomationElement> current = hit;
        for (int depth = 0; depth <= MaximumParentSteps && current; depth++) {
            if (Reader::process_of(current.get()) != process) break;
            if (Reader::is_password(current.get())) return Protected;
            if (read_text_at_point(current.get(), depth)) return Ok;
            winrt::com_ptr<IUIAutomationElement> parent;
            if (FAILED(walker_->GetParentElement(current.get(), parent.put()))) break;
            current = parent;
        }

        // Otherwise the element's own name, or the nearest named parent (not a window or pane).
        current = hit;
        for (int depth = 0; depth <= 3 && current; depth++) {
            if (Reader::process_of(current.get()) != process) break;
            CONTROLTYPEID type = 0;
            current->get_CurrentControlType(&type);
            if (type == UIA_WindowControlTypeId || type == UIA_PaneControlTypeId || type == UIA_DocumentControlTypeId) break;
            if (read_name(current.get(), type)) { note(L"name@" + std::to_wstring(depth) + L";"); return Ok; }
            winrt::com_ptr<IUIAutomationElement> parent;
            if (FAILED(walker_->GetParentElement(current.get(), parent.put()))) break;
            current = parent;
        }
        return Unavailable;
    }

private:
    // Paragraphs longer than this are usually whole documents from providers without paragraphs.
    static constexpr size_t LongParagraph = 4000;
    POINT point_;
    uint32_t maximum_;
    DWORD excluded_;
    winrt::com_ptr<IUIAutomationTreeWalker> walker_;

    void note(std::wstring const& part) {
        if (diagnostic.size() + part.size() <= MaximumDiagnostic) diagnostic += part;
    }

    std::wstring range_text(IUIAutomationTextRange* origin, TextUnit unit) {
        winrt::com_ptr<IUIAutomationTextRange> range;
        if (FAILED(origin->Clone(range.put())) || !range) return {};
        if (FAILED(range->ExpandToEnclosingUnit(unit))) return {};
        Bstr value;
        if (FAILED(range->GetText(static_cast<int>(maximum_) + 1, value.put()))) return {};
        if (value.size() > maximum_) throw TooLongError{};
        return std::wstring(value.value, value.size());
    }

    bool read_text_at_point(IUIAutomationElement* element, int depth) {
        winrt::com_ptr<IUIAutomationTextPattern> pattern;
        if (FAILED(element->GetCurrentPatternAs(UIA_TextPatternId, __uuidof(IUIAutomationTextPattern), pattern.put_void())) || !pattern) return false;
        winrt::com_ptr<IUIAutomationTextRange> range;
        if (FAILED(pattern->RangeFromPoint(point_, range.put())) || !range) { note(L"text@" + std::to_wstring(depth) + L":no-range;"); return false; }
        std::wstring value = range_text(range.get(), TextUnit_Paragraph);
        const wchar_t* unit = L"paragraph";
        if (is_blank(value) || value.size() > LongParagraph) {
            std::wstring line = range_text(range.get(), TextUnit_Line);
            if (!is_blank(line)) { value = std::move(line); unit = L"line"; }
        }
        if (is_blank(value)) { note(L"text@" + std::to_wstring(depth) + L":empty;"); return false; }
        note(L"text@" + std::to_wstring(depth) + L":" + unit + L";");
        text = std::move(value);
        return true;
    }

    bool read_name(IUIAutomationElement* element, CONTROLTYPEID type) {
        Bstr name;
        std::wstring value;
        if (SUCCEEDED(element->get_CurrentName(name.put())) && name.size() > 0) value.assign(name.value, name.size());
        if (is_blank(value) && (type == UIA_EditControlTypeId || type == UIA_ComboBoxControlTypeId)) {
            winrt::com_ptr<IUIAutomationValuePattern> pattern;
            Bstr current;
            if (SUCCEEDED(element->GetCurrentPatternAs(UIA_ValuePatternId, __uuidof(IUIAutomationValuePattern), pattern.put_void())) && pattern
                && SUCCEEDED(pattern->get_CurrentValue(current.put())) && current.size() > 0)
                value.assign(current.value, current.size());
        }
        if (is_blank(value)) return false;
        Bstr help;
        if (SUCCEEDED(element->get_CurrentHelpText(help.put())) && help.size() > 0) {
            std::wstring extra(help.value, help.size());
            if (!is_blank(extra) && extra != value) value += L". " + extra;
        }
        if (value.size() > maximum_) throw TooLongError{};
        text = std::move(value);
        return true;
    }
};
}

// Returns a Status value; text is set only on Ok. Elements of the excluded process (the app) are
// ignored, so its own indicator never answers. Both strings are freed with qs_free.
extern "C" __declspec(dllexport) int __cdecl qs_read_at_point(int x, int y, uint32_t maximum, uint32_t excluded_process, wchar_t** text, wchar_t** diagnostic) noexcept {
    *text = nullptr;
    *diagnostic = nullptr;
    PointReader reader(POINT{ x, y }, maximum, excluded_process);
    Status status;
    try { status = reader.Run(); }
    catch (TooLongError const&) { status = TooLong; }
    catch (winrt::hresult_error const& error) {
        status = error.code() == E_ACCESSDENIED ? Denied : ProviderFailure;
        wchar_t code[16];
        swprintf_s(code, L"hr=%08X;", static_cast<unsigned>(error.code().value));
        reader.diagnostic += code;
    }
    catch (...) { status = ProviderFailure; }
    try {
        if (status == Ok) copy_out(reader.text, text);
        copy_out(reader.diagnostic, diagnostic);
    }
    catch (...) { status = ProviderFailure; }
    std::fill(reader.text.begin(), reader.text.end(), L'_');
    return status;
}

// Returns a Status value. text receives the selection only on Ok; diagnostic is always set when
// possible. Both are freed with qs_free.
extern "C" __declspec(dllexport) int __cdecl qs_read_selection(HWND source, uint32_t maximum, wchar_t** text, wchar_t** diagnostic) noexcept {
    *text = nullptr;
    *diagnostic = nullptr;
    Reader reader(source, maximum);
    Status status;
    try { status = reader.Run(); }
    catch (TooLongError const&) { status = TooLong; }
    catch (winrt::hresult_error const& error) {
        status = error.code() == E_ACCESSDENIED ? Denied : ProviderFailure;
        wchar_t code[16];
        swprintf_s(code, L"hr=%08X;", static_cast<unsigned>(error.code().value));
        reader.diagnostic += code;
    }
    catch (...) { status = ProviderFailure; }
    try {
        if (status == Ok) copy_out(reader.text, text);
        copy_out(reader.diagnostic, diagnostic);
    }
    catch (...) { status = ProviderFailure; }
    std::fill(reader.text.begin(), reader.text.end(), L'_'); // No copy of the selection stays here.
    return status;
}
