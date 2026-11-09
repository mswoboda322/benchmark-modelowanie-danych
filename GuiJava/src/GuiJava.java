// GuiJava – aplikacja okienkowa K3 w Javie (Swing, JDK 25).
//
// Użycie i format wyniku – jak w GuiCpp/main.cpp:
//   java -jar GuiJava.jar --bench <startup|fill|draw|responsive> --out <plik.csv>
//                         [--variant idle|ui-thread|worker] [--param n] [--iter 10] [--warmup 2]
//   język;scenariusz;wariant;parametr;iteracja;rozgrzewka;metryka;wartość

import java.awt.Dimension;
import java.awt.Graphics;
import java.awt.Toolkit;
import java.awt.image.BufferedImage;
import java.awt.image.DataBufferInt;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import javax.swing.DefaultListModel;
import javax.swing.JButton;
import javax.swing.JFrame;
import javax.swing.JList;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.SwingUtilities;
import javax.swing.Timer;
import javax.swing.WindowConstants;

public final class GuiJava {
    // Układ okna – taki sam w GuiCpp i GuiCs.
    private static final int IMAGE_SIZE = 800, LIST_WIDTH = 300, MARGIN = 10, BUTTON_HEIGHT = 30;
    private static final int DRAW_MAX_ITER = 256, WORK_SIZE = 1000, WORK_MAX_ITER = 1000;
    private static final int PROGRESS_EVERY_ROWS = 50, PROBE_INTERVAL_MS = 10, STARTUP_HOLD_MS = 1000;

    record Options(String bench, String variant, long param, int iter, int warmup, String out) {
        static Options parse(String[] args) {
            String bench = null, out = null, variant = "-";
            long param = 0;
            int iter = 10, warmup = 2;
            for (int i = 0; i + 1 < args.length; i += 2) {
                String value = args[i + 1];
                switch (args[i]) {
                    case "--bench" -> bench = value;
                    case "--variant" -> variant = value;
                    case "--param" -> param = Long.parseLong(value);
                    case "--iter" -> iter = Integer.parseInt(value);
                    case "--warmup" -> warmup = Integer.parseInt(value);
                    case "--out" -> out = value;
                    default -> { }
                }
            }
            return bench == null || out == null ? null : new Options(bench, variant, param, iter, warmup, out);
        }
    }

    private final Options o;
    private final JFrame frame = new JFrame("Bench GUI – Java");
    private final JList<String> list = new JList<>(new DefaultListModel<>());
    private final JScrollPane scroll = new JScrollPane(list);
    private final List<String> lines = new ArrayList<>();
    private boolean firstPaintDone;
    private long firstPaintNs;
    private BufferedImage image;

    private final JPanel canvas = new JPanel(null) {
        @Override
        protected void paintComponent(Graphics g) {
            super.paintComponent(g);
            if (image != null) g.drawImage(image, 0, 0, null);
        }
    };

    // responsive
    private final List<Long> latencies = new ArrayList<>();
    private long endNs;
    private volatile boolean stop;
    private volatile int workerImages;
    private int uiImages;
    private Thread probe, worker;
    private final int[] uiImage = new int[WORK_SIZE * WORK_SIZE];

    public static void main(String[] args) {
        // Bez skalowania DPI – tak jak GuiCpp i GuiCs. Musi być ustawione przed inicjalizacją AWT.
        System.setProperty("sun.java2d.uiScale", "1");
        Options o = Options.parse(args);
        if (o == null) {
            System.err.println("Uzycie: java -jar GuiJava.jar --bench <scenariusz> --out <plik> [--variant v] [--param n]");
            System.exit(1);
        }
        SwingUtilities.invokeLater(() -> new GuiJava(o).show());
    }

    private GuiJava(Options o) {
        this.o = o;
    }

    private void show() {
        JPanel content = new JPanel(null) {
            @Override
            protected void paintComponent(Graphics g) {
                super.paintComponent(g);
                if (!firstPaintDone) {
                    firstPaintDone = true;
                    firstPaintNs = System.nanoTime(); // na Windows: QueryPerformanceCounter
                    SwingUtilities.invokeLater(GuiJava.this::startScenario);
                }
            }
        };
        content.setPreferredSize(new Dimension(MARGIN * 3 + LIST_WIDTH + IMAGE_SIZE, MARGIN * 2 + IMAGE_SIZE));
        scroll.setBounds(MARGIN, MARGIN, LIST_WIDTH, IMAGE_SIZE - BUTTON_HEIGHT - MARGIN);
        JButton button = new JButton("Start");
        button.setBounds(MARGIN, MARGIN + IMAGE_SIZE - BUTTON_HEIGHT, LIST_WIDTH, BUTTON_HEIGHT);
        canvas.setBounds(MARGIN * 2 + LIST_WIDTH, MARGIN, IMAGE_SIZE, IMAGE_SIZE);
        content.add(scroll);
        content.add(button);
        content.add(canvas);

        frame.setContentPane(content);
        frame.setDefaultCloseOperation(WindowConstants.EXIT_ON_CLOSE);
        frame.setResizable(false);
        frame.pack();
        frame.setVisible(true);
    }

    private void record(int iteration, boolean warmup, String metric, long value) {
        lines.add("Java;" + o.bench() + ";" + o.variant() + ";" + o.param() + ";" + iteration + ";"
                + (warmup ? 1 : 0) + ";" + metric + ";" + value);
    }

    /** Zapis wyników i zamknięcie aplikacji. */
    private void finish() {
        try {
            Files.write(Path.of(o.out()), lines, StandardCharsets.UTF_8);
        } catch (IOException e) {
            e.printStackTrace();
        }
        frame.dispose();
        System.exit(0);
    }

    private void startScenario() {
        switch (o.bench()) {
            case "startup" -> {
                record(1, false, "first_paint_ns", firstPaintNs);
                Timer timer = new Timer(STARTUP_HOLD_MS, e -> finish());
                timer.setRepeats(false);
                timer.start();
            }
            case "fill" -> runFill();
            case "draw" -> runDraw();
            case "responsive" -> startResponsive();
            default -> finish();
        }
    }

    static void mandelRow(int[] img, int width, int height, int y, int maxIter) {
        double cy = -1.5 + 3.0 * y / height;
        for (int x = 0; x < width; x++) {
            double cx = -2.0 + 3.0 * x / width;
            double zx = 0.0, zy = 0.0;
            int it = 0;
            while (it < maxIter) {
                double zx2 = zx * zx, zy2 = zy * zy;
                if (zx2 + zy2 > 4.0) break;
                zy = 2.0 * zx * zy + cy;
                zx = zx2 - zy2 + cx;
                it++;
            }
            img[y * width + x] = it;
        }
    }

    // ---------- fill ----------
    private void runFill() {
        int n = (int) o.param();
        for (int it = 1; it <= o.warmup() + o.iter(); it++) {
            list.setModel(new DefaultListModel<>());
            scroll.validate();
            scroll.paintImmediately(0, 0, scroll.getWidth(), scroll.getHeight());

            long t0 = System.nanoTime();
            // Model wypełniany przed podłączeniem do listy – odpowiednik BeginUpdate/WM_SETREDRAW.
            DefaultListModel<String> model = new DefaultListModel<>();
            for (int i = 0; i < n; i++) model.addElement("Element " + i);
            list.setModel(model);
            scroll.validate(); // synchroniczny układ (JList liczy rozmiar elementów)
            scroll.paintImmediately(0, 0, scroll.getWidth(), scroll.getHeight());
            Toolkit.getDefaultToolkit().sync();
            record(it, it <= o.warmup(), "time_ns", System.nanoTime() - t0);
        }
        finish();
    }

    // ---------- draw ----------
    private void runDraw() {
        int frames = (int) o.param();
        int count = IMAGE_SIZE * IMAGE_SIZE;
        int[] iters = new int[count];
        for (int y = 0; y < IMAGE_SIZE; y++) mandelRow(iters, IMAGE_SIZE, IMAGE_SIZE, y, DRAW_MAX_ITER);
        int[] palette = new int[256];
        for (int i = 0; i < 256; i++) palette[i] = (i << 16) | (((i * 5) & 255) << 8) | ((i * 11) & 255);
        image = new BufferedImage(IMAGE_SIZE, IMAGE_SIZE, BufferedImage.TYPE_INT_RGB);
        int[] pixels = ((DataBufferInt) image.getRaster().getDataBuffer()).getData();

        for (int it = 1; it <= o.warmup() + o.iter(); it++) {
            long t0 = System.nanoTime();
            for (int f = 0; f < frames; f++) {
                for (int i = 0; i < count; i++) pixels[i] = palette[(iters[i] + f) & 255];
                canvas.paintImmediately(0, 0, IMAGE_SIZE, IMAGE_SIZE);
            }
            Toolkit.getDefaultToolkit().sync();
            record(it, it <= o.warmup(), "time_ns", System.nanoTime() - t0);
        }
        finish();
    }

    // ---------- responsive ----------
    private void startResponsive() {
        endNs = System.nanoTime() + o.param() * 1_000_000L;
        // Sonda: co 10 ms wysyła do wątku UI (EDT) zdarzenie ze znacznikiem czasu.
        probe = new Thread(() -> {
            while (System.nanoTime() < endNs) {
                try {
                    Thread.sleep(PROBE_INTERVAL_MS);
                } catch (InterruptedException e) {
                    return;
                }
                long ts = System.nanoTime();
                SwingUtilities.invokeLater(() -> latencies.add(System.nanoTime() - ts));
            }
            SwingUtilities.invokeLater(this::stopResponsive);
        });
        probe.setDaemon(true);
        probe.start();

        if (o.variant().equals("ui-thread")) {
            SwingUtilities.invokeLater(this::computeOnUiThread);
        } else if (o.variant().equals("worker")) {
            worker = new Thread(() -> {
                int[] img = new int[WORK_SIZE * WORK_SIZE];
                while (!stop) {
                    for (int y = 0; y < WORK_SIZE && !stop; y++) {
                        mandelRow(img, WORK_SIZE, WORK_SIZE, y, WORK_MAX_ITER);
                        if ((y + 1) % PROGRESS_EVERY_ROWS == 0) {
                            int row = y + 1;
                            SwingUtilities.invokeLater(
                                    () -> frame.setTitle("Bench GUI – Java – wiersz " + row + "/" + WORK_SIZE));
                        }
                    }
                    if (!stop) workerImages++;
                }
            });
            worker.setDaemon(true);
            worker.start();
        }
    }

    /** Obliczenia na wątku UI (błędna praktyka): cały obraz w jednym zdarzeniu. */
    private void computeOnUiThread() {
        if (System.nanoTime() >= endNs) return;
        for (int y = 0; y < WORK_SIZE; y++) mandelRow(uiImage, WORK_SIZE, WORK_SIZE, y, WORK_MAX_ITER);
        uiImages++;
        SwingUtilities.invokeLater(this::computeOnUiThread);
    }

    private void stopResponsive() {
        stop = true;
        try {
            probe.join();
            if (worker != null) worker.join();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        for (int i = 0; i < latencies.size(); i++) record(i + 1, false, "latency_ns", latencies.get(i));
        record(0, false, "images", uiImages + workerImages);
        finish();
    }
}
