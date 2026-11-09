// GuiCpp – aplikacja okienkowa K3 w C++ (Win32 API).
//
// Użycie: GuiCpp.exe --bench <startup|fill|draw|responsive> --out <plik.csv>
//                    [--variant idle|ui-thread|worker] [--param n] [--iter 10] [--warmup 2]
//   startup     – znacznik czasu pierwszego rysowania okna, po 1 s zamknięcie
//   fill        – dodanie <param> napisów do listy (do odrysowania)
//   draw        – <param> klatek obrazu 800×800 z bufora pikseli
//   responsive  – opóźnienie obsługi zdarzeń przez <param> ms przy obliczeniach (wariant)
// Wynik (plik --out), jedna linia na wartość:
//   język;scenariusz;wariant;parametr;iteracja;rozgrzewka;metryka;wartość
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

namespace
{
    // Układ okna – taki sam w GuiCs i GuiJava.
    constexpr int ImageSize = 800, ListWidth = 300, Margin = 10, ButtonHeight = 30;
    constexpr int ClientWidth = Margin * 3 + ListWidth + ImageSize;
    constexpr int ClientHeight = Margin * 2 + ImageSize;
    constexpr int DrawMaxIter = 256;    // obraz w scenariuszu draw
    constexpr int WorkSize = 1000;      // obraz liczony w scenariuszu responsive
    constexpr int WorkMaxIter = 1000;
    constexpr int ProgressEveryRows = 50;
    constexpr int ProbeIntervalMs = 10;
    constexpr int StartupHoldMs = 1000;

    constexpr UINT WmStart = WM_APP + 1, WmProbe = WM_APP + 2, WmCompute = WM_APP + 3, WmProgress = WM_APP + 4,
                   WmStop = WM_APP + 5;
    constexpr UINT_PTR TimerExit = 1;

    struct Options
    {
        std::string bench, variant = "-";
        std::wstring out;
        long long param = 0;
        int iter = 10, warmup = 2;
    } g_opt;

    HWND g_main = nullptr, g_list = nullptr;
    bool g_firstPaintDone = false;
    long long g_firstPaintNs = 0;
    std::vector<std::string> g_lines;

    std::vector<uint32_t> g_pixels; // ostatnia klatka (rysowana też w WM_PAINT)
    BITMAPINFO g_bmi{};

    // responsive
    std::vector<long long> g_latencies;
    long long g_endNs = 0;
    std::atomic<bool> g_stop{false};
    std::atomic<int> g_workerImages{0};
    int g_uiImages = 0;
    std::thread g_probe, g_worker;

    long long nowNs()
    {
        static const long long freq = [] {
            LARGE_INTEGER f;
            QueryPerformanceFrequency(&f);
            return f.QuadPart;
        }();
        LARGE_INTEGER c;
        QueryPerformanceCounter(&c);
        return c.QuadPart / freq * 1'000'000'000LL + c.QuadPart % freq * 1'000'000'000LL / freq;
    }

    void record(int iteration, bool warmup, const char *metric, long long value)
    {
        char buf[256];
        std::snprintf(buf, sizeof buf, "C++;%s;%s;%lld;%d;%d;%s;%lld\n", g_opt.bench.c_str(), g_opt.variant.c_str(),
                      g_opt.param, iteration, warmup ? 1 : 0, metric, value);
        g_lines.emplace_back(buf);
    }

    // Zapis wyników i zamknięcie aplikacji.
    void finish()
    {
        if (FILE *f = _wfopen(g_opt.out.c_str(), L"wb"))
        {
            for (const auto &line : g_lines)
                std::fputs(line.c_str(), f);
            std::fclose(f);
        }
        DestroyWindow(g_main);
    }

    void mandelRow(int *img, int width, int height, int y, int maxIter)
    {
        const double cy = -1.5 + 3.0 * y / height;
        for (int x = 0; x < width; x++)
        {
            const double cx = -2.0 + 3.0 * x / width;
            double zx = 0.0, zy = 0.0;
            int it = 0;
            while (it < maxIter)
            {
                const double zx2 = zx * zx, zy2 = zy * zy;
                if (zx2 + zy2 > 4.0)
                    break;
                zy = 2.0 * zx * zy + cy;
                zx = zx2 - zy2 + cx;
                it++;
            }
            img[static_cast<size_t>(y) * width + x] = it;
        }
    }

    // ---------- fill ----------
    void runFill()
    {
        const int n = static_cast<int>(g_opt.param);
        wchar_t text[32];
        for (int it = 1; it <= g_opt.warmup + g_opt.iter; it++)
        {
            SendMessageW(g_list, LB_RESETCONTENT, 0, 0);
            UpdateWindow(g_list);

            const long long t0 = nowNs();
            SendMessageW(g_list, WM_SETREDRAW, FALSE, 0);
            for (int i = 0; i < n; i++)
            {
                swprintf(text, 32, L"Element %d", i);
                SendMessageW(g_list, LB_ADDSTRING, 0, reinterpret_cast<LPARAM>(text));
            }
            SendMessageW(g_list, WM_SETREDRAW, TRUE, 0);
            InvalidateRect(g_list, nullptr, TRUE);
            UpdateWindow(g_list); // synchroniczne odrysowanie listy
            record(it, it <= g_opt.warmup, "time_ns", nowNs() - t0);
        }
        finish();
    }

    // ---------- draw ----------
    void runDraw()
    {
        const int frames = static_cast<int>(g_opt.param);
        const size_t count = static_cast<size_t>(ImageSize) * ImageSize;
        std::vector<int> iters(count);
        for (int y = 0; y < ImageSize; y++)
            mandelRow(iters.data(), ImageSize, ImageSize, y, DrawMaxIter);
        uint32_t palette[256];
        for (uint32_t i = 0; i < 256; i++)
            palette[i] = (i << 16) | (((i * 5) & 255) << 8) | ((i * 11) & 255); // 0x00RRGGBB
        g_pixels.assign(count, 0);

        HDC dc = GetDC(g_main);
        for (int it = 1; it <= g_opt.warmup + g_opt.iter; it++)
        {
            const long long t0 = nowNs();
            for (int f = 0; f < frames; f++)
            {
                for (size_t i = 0; i < count; i++)
                    g_pixels[i] = palette[(iters[i] + f) & 255];
                SetDIBitsToDevice(dc, Margin * 2 + ListWidth, Margin, ImageSize, ImageSize, 0, 0, 0, ImageSize,
                                  g_pixels.data(), &g_bmi, DIB_RGB_COLORS);
            }
            GdiFlush();
            record(it, it <= g_opt.warmup, "time_ns", nowNs() - t0);
        }
        ReleaseDC(g_main, dc);
        finish();
    }

    // ---------- responsive ----------
    void startResponsive()
    {
        g_endNs = nowNs() + g_opt.param * 1'000'000LL;
        // Sonda: co 10 ms wysyła do wątku UI komunikat ze znacznikiem czasu.
        g_probe = std::thread([] {
            while (nowNs() < g_endNs)
            {
                Sleep(ProbeIntervalMs);
                PostMessageW(g_main, WmProbe, 0, static_cast<LPARAM>(nowNs()));
            }
            PostMessageW(g_main, WmStop, 0, 0);
        });
        if (g_opt.variant == "ui-thread")
            PostMessageW(g_main, WmCompute, 0, 0);
        else if (g_opt.variant == "worker")
            g_worker = std::thread([] {
                std::vector<int> img(static_cast<size_t>(WorkSize) * WorkSize);
                while (!g_stop)
                {
                    for (int y = 0; y < WorkSize && !g_stop; y++)
                    {
                        mandelRow(img.data(), WorkSize, WorkSize, y, WorkMaxIter);
                        if ((y + 1) % ProgressEveryRows == 0)
                            PostMessageW(g_main, WmProgress, static_cast<WPARAM>(y + 1), 0);
                    }
                    if (!g_stop)
                        g_workerImages++;
                }
            });
    }

    // Obliczenia na wątku UI (błędna praktyka): cały obraz w jednym komunikacie.
    void computeOnUiThread()
    {
        if (nowNs() >= g_endNs)
            return;
        static std::vector<int> img(static_cast<size_t>(WorkSize) * WorkSize);
        for (int y = 0; y < WorkSize; y++)
            mandelRow(img.data(), WorkSize, WorkSize, y, WorkMaxIter);
        g_uiImages++;
        PostMessageW(g_main, WmCompute, 0, 0);
    }

    void stopResponsive()
    {
        g_stop = true;
        g_probe.join();
        if (g_worker.joinable())
            g_worker.join();
        for (size_t i = 0; i < g_latencies.size(); i++)
            record(static_cast<int>(i) + 1, false, "latency_ns", g_latencies[i]);
        record(0, false, "images", g_uiImages + g_workerImages.load());
        finish();
    }

    void startScenario()
    {
        if (g_opt.bench == "startup")
        {
            record(1, false, "first_paint_ns", g_firstPaintNs);
            SetTimer(g_main, TimerExit, StartupHoldMs, nullptr);
        }
        else if (g_opt.bench == "fill")
            runFill();
        else if (g_opt.bench == "draw")
            runDraw();
        else if (g_opt.bench == "responsive")
            startResponsive();
        else
            finish();
    }

    LRESULT CALLBACK wndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            if (!g_pixels.empty())
                SetDIBitsToDevice(dc, Margin * 2 + ListWidth, Margin, ImageSize, ImageSize, 0, 0, 0, ImageSize,
                                  g_pixels.data(), &g_bmi, DIB_RGB_COLORS);
            EndPaint(hwnd, &ps);
            if (!g_firstPaintDone)
            {
                g_firstPaintDone = true;
                g_firstPaintNs = nowNs();
                PostMessageW(hwnd, WmStart, 0, 0);
            }
            return 0;
        }
        case WmStart:
            startScenario();
            return 0;
        case WmProbe:
            g_latencies.push_back(nowNs() - static_cast<long long>(lParam));
            return 0;
        case WmCompute:
            computeOnUiThread();
            return 0;
        case WmProgress:
        {
            wchar_t title[64];
            swprintf(title, 64, L"Bench GUI – C++ – wiersz %d/%d", static_cast<int>(wParam), WorkSize);
            SetWindowTextW(hwnd, title);
            return 0;
        }
        case WmStop:
            stopResponsive();
            return 0;
        case WM_TIMER:
            if (wParam == TimerExit)
            {
                KillTimer(hwnd, TimerExit);
                finish();
            }
            return 0;
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    std::string narrow(const wchar_t *s)
    {
        std::string r;
        for (; *s; s++)
            r.push_back(static_cast<char>(*s)); // argumenty są w ASCII
        return r;
    }

    bool parseArgs()
    {
        int argc = 0;
        LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        for (int i = 1; i + 1 < argc; i += 2)
        {
            const std::wstring key = argv[i];
            const wchar_t *value = argv[i + 1];
            if (key == L"--bench")
                g_opt.bench = narrow(value);
            else if (key == L"--variant")
                g_opt.variant = narrow(value);
            else if (key == L"--param")
                g_opt.param = _wtoll(value);
            else if (key == L"--iter")
                g_opt.iter = _wtoi(value);
            else if (key == L"--warmup")
                g_opt.warmup = _wtoi(value);
            else if (key == L"--out")
                g_opt.out = value;
        }
        LocalFree(argv);
        return !g_opt.bench.empty() && !g_opt.out.empty();
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int showCmd)
{
    if (!parseArgs())
    {
        MessageBoxW(nullptr, L"Użycie: GuiCpp.exe --bench <scenariusz> --out <plik> [--variant v] [--param n]",
                    L"GuiCpp", MB_ICONERROR);
        return 1;
    }

    g_bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    g_bmi.bmiHeader.biWidth = ImageSize;
    g_bmi.bmiHeader.biHeight = -ImageSize; // wiersze od góry
    g_bmi.bmiHeader.biPlanes = 1;
    g_bmi.bmiHeader.biBitCount = 32;
    g_bmi.bmiHeader.biCompression = BI_RGB;

    WNDCLASSEXW wc{sizeof(WNDCLASSEXW)};
    wc.lpfnWndProc = wndProc;
    wc.hInstance = instance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    wc.lpszClassName = L"BenchGuiCpp";
    RegisterClassExW(&wc);

    const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    RECT rc{0, 0, ClientWidth, ClientHeight};
    AdjustWindowRect(&rc, style, FALSE);
    g_main = CreateWindowExW(0, wc.lpszClassName, L"Bench GUI – C++", style, CW_USEDEFAULT, CW_USEDEFAULT,
                             rc.right - rc.left, rc.bottom - rc.top, nullptr, nullptr, instance, nullptr);

    HFONT font = static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
    g_list = CreateWindowExW(WS_EX_CLIENTEDGE, L"LISTBOX", nullptr,
                             WS_CHILD | WS_VISIBLE | WS_VSCROLL | LBS_NOINTEGRALHEIGHT | LBS_NOTIFY, Margin, Margin,
                             ListWidth, ImageSize - ButtonHeight - Margin, g_main, nullptr, instance, nullptr);
    HWND button = CreateWindowExW(0, L"BUTTON", L"Start", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, Margin,
                                  Margin + ImageSize - ButtonHeight, ListWidth, ButtonHeight, g_main, nullptr,
                                  instance, nullptr);
    SendMessageW(g_list, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    SendMessageW(button, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);

    ShowWindow(g_main, showCmd);
    UpdateWindow(g_main);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}
