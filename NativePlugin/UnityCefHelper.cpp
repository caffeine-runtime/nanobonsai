// UnityCefHelper: the CEF subprocess executable; on macOS loads the framework from Contents/Frameworks and runs as a helper, on Windows runs CefExecuteProcess for the browser's child processes.
#ifdef _WIN32
#include <windows.h>
#endif
#include "include/cef_app.h"
#ifdef __APPLE__
#include "include/wrapper/cef_library_loader.h"
#endif

int main(int argc, char* argv[]) {
#ifdef __APPLE__
    // The helper app sits in Contents/Frameworks beside the framework it loads.
    CefScopedLibraryLoader library_loader;
    if (!library_loader.LoadInHelper()) return 1;
    CefMainArgs main_args(argc, argv);
#else
    CefMainArgs main_args(GetModuleHandle(NULL));
#endif
    return CefExecuteProcess(main_args, nullptr, nullptr);
}
