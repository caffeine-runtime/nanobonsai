// macOS half of the CEF bridge: loads the framework from the app's Contents/Frameworks, gives Unity's NSApp the
// CefAppProtocol CEF aborts without, and turns the view's Windows key codes into the Mac key events CEF reads.
#import <Cocoa/Cocoa.h>
#include <objc/runtime.h>
#include <string>
#include "include/cef_application_mac.h"
#include "include/cef_browser.h"
#include "include/wrapper/cef_library_loader.h"

namespace {
BOOL g_handling_send_event = NO;
IMP g_original_send_event = nullptr;

BOOL IsHandlingSendEvent(id, SEL) { return g_handling_send_event; }
void SetHandlingSendEvent(id, SEL, BOOL value) { g_handling_send_event = value; }

void SendEvent(id self, SEL command, NSEvent* event) {
    BOOL handling = g_handling_send_event;
    g_handling_send_event = YES;
    reinterpret_cast<void (*)(id, SEL, NSEvent*)>(g_original_send_event)(self, command, event);
    g_handling_send_event = handling;
}
}  // namespace

extern "C" bool UnityCefLoadFramework() {
    NSString* frameworks = [[NSBundle mainBundle] privateFrameworksPath];
    std::string path = std::string([frameworks fileSystemRepresentation]) +
        "/Chromium Embedded Framework.framework/Chromium Embedded Framework";
    return cef_load_library(path.c_str()) == 1;
}

// Unity owns the NSApplication subclass, so the protocol and its send-event flag are added to it at runtime.
extern "C" void UnityCefAdoptApplication() {
    Class application = [NSApp class];
    if (class_conformsToProtocol(application, @protocol(CefAppProtocol))) return;
    std::string getter = std::string(@encode(BOOL)) + "@:";
    std::string setter = std::string("v@:") + @encode(BOOL);
    class_addMethod(application, @selector(isHandlingSendEvent), reinterpret_cast<IMP>(IsHandlingSendEvent), getter.c_str());
    class_addMethod(application, @selector(setHandlingSendEvent:), reinterpret_cast<IMP>(SetHandlingSendEvent), setter.c_str());
    Method send = class_getInstanceMethod(application, @selector(sendEvent:));
    g_original_send_event = method_getImplementation(send);
    if (!class_addMethod(application, @selector(sendEvent:), reinterpret_cast<IMP>(SendEvent), method_getTypeEncoding(send)))
        method_setImplementation(send, reinterpret_cast<IMP>(SendEvent));
    class_addProtocol(application, @protocol(CrAppProtocol));
    class_addProtocol(application, @protocol(CrAppControlProtocol));
    class_addProtocol(application, @protocol(CefAppProtocol));
}

// CEF on macOS builds its key event from native_key_code and the characters, ignoring windows_key_code.
// False drops the event: AppKit reports control and function keys to text input too, and the raw key events carry them.
bool TranslateMacKey(CefKeyEvent& key_event) {
    if (key_event.type == KEYEVENT_CHAR) {
        int c = key_event.windows_key_code;
        if (c < 0x20 || c == 0x7F || (c >= 0xF700 && c <= 0xF8FF)) return false;
        key_event.character = key_event.unmodified_character = static_cast<char16_t>(c);
        return true;
    }
    struct MacKey { int windows, native; char16_t character; };
    // Shift's zero character makes CEF send a modifier-flags event.
    static const MacKey keys[] = {
        {0x08, 0x33, 0x7F}, {0x0D, 0x24, 0x0D}, {0x2E, 0x75, 0xF728}, {0x09, 0x30, 0x09},
        {0x25, 0x7B, 0xF702}, {0x27, 0x7C, 0xF703}, {0x26, 0x7E, 0xF700}, {0x28, 0x7D, 0xF701},
        {0x1B, 0x35, 0x1B}, {0x10, 0x38, 0}, {0xDC, 0x2A, u'\\'},
    };
    for (const MacKey& key : keys) {
        if (key.windows != key_event.windows_key_code) continue;
        key_event.native_key_code = key.native;
        key_event.character = key_event.unmodified_character = key.character;
        return true;
    }
    return false;
}
