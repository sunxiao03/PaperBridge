from pathlib import Path

from reportlab.lib.colors import HexColor, white
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen.canvas import Canvas


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "output" / "pdf" / "paperbridge-500-page-benchmark.pdf"
PAGE_COUNT = 500


def draw_header(canvas: Canvas, page_number: int) -> None:
    width, height = letter
    canvas.setFillColor(HexColor("#173247"))
    canvas.rect(0, height - 48, width, 48, fill=1, stroke=0)
    canvas.setFillColor(white)
    canvas.setFont("Helvetica-Bold", 11)
    canvas.drawString(38, height - 30, "PaperBridge synthetic long-document benchmark")
    canvas.setFont("Helvetica", 9)
    canvas.drawRightString(width - 38, height - 30, f"Page {page_number} / {PAGE_COUNT}")


def draw_single_column(canvas: Canvas, page_number: int) -> None:
    _, height = letter
    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 16)
    canvas.drawString(42, height - 82, f"Section {page_number}: neutron transport benchmark")
    canvas.setFillColor(HexColor("#20272C"))
    canvas.setFont("Times-Roman", 10)
    y = height - 110
    for line in range(30):
        canvas.drawString(
            42,
            y,
            f"Line {line + 1:02d}. The synthetic neutron flux remains bounded in benchmark case {page_number:03d}.",
        )
        y -= 19


def draw_two_columns(canvas: Canvas, page_number: int) -> None:
    width, height = letter
    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 16)
    canvas.drawString(42, height - 82, f"Two-column case {page_number}")
    canvas.setFillColor(HexColor("#20272C"))
    canvas.setFont("Times-Roman", 9)
    column_width = (width - 108) / 2
    for column in range(2):
        x = 42 + column * (column_width + 24)
        y = height - 110
        for line in range(32):
            canvas.drawString(
                x,
                y,
                f"C{column + 1}-{line + 1:02d}: delayed neutron data for case {page_number:03d}.",
            )
            y -= 18


def draw_table_page(canvas: Canvas, page_number: int) -> None:
    width, height = letter
    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 16)
    canvas.drawString(42, height - 82, f"Parameter table {page_number}")
    left = 42
    top = height - 108
    row_height = 24
    table_width = width - 84
    for row in range(24):
        y = top - row_height * (row + 1)
        canvas.setFillColor(HexColor("#E9EFF2") if row % 2 else white)
        canvas.rect(left, y, table_width, row_height, fill=1, stroke=1)
        canvas.setFillColor(HexColor("#20272C"))
        canvas.setFont("Helvetica", 9)
        canvas.drawString(left + 8, y + 8, f"Group constant {row + 1}")
        canvas.drawString(left + 230, y + 8, f"{page_number * 0.001 + row * 0.01:.5f}")
        canvas.drawString(left + 360, y + 8, "synthetic unit")


def create_fixture() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    canvas = Canvas(str(OUTPUT), pagesize=letter, pageCompression=1, invariant=1)
    canvas.setTitle("PaperBridge 500-page synthetic benchmark")
    canvas.setAuthor("PaperBridge contributors")
    canvas.setSubject("Generated long-document performance fixture")
    canvas.setCreator("PaperBridge long fixture generator v1")
    canvas.setKeywords("PDFium, performance, synthetic, long document")

    for page_number in range(1, PAGE_COUNT + 1):
        bookmark = f"page-{page_number}"
        canvas.bookmarkPage(bookmark)
        if page_number == 1 or page_number % 50 == 0:
            canvas.addOutlineEntry(f"Benchmark section {page_number}", bookmark, level=0, closed=False)

        draw_header(canvas, page_number)
        layout = page_number % 3
        if layout == 0:
            draw_table_page(canvas, page_number)
        elif layout == 1:
            draw_single_column(canvas, page_number)
        else:
            draw_two_columns(canvas, page_number)

        canvas.setFillColor(HexColor("#68757E"))
        canvas.setFont("Helvetica-Oblique", 8)
        canvas.drawString(42, 26, "Generated locally; contains no external publication content.")
        canvas.showPage()

    canvas.save()


if __name__ == "__main__":
    create_fixture()
