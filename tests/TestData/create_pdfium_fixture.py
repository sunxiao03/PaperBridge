from pathlib import Path

from reportlab.lib.colors import HexColor, white
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen.canvas import Canvas


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "output" / "pdf" / "pdfium-text-layer-sample.pdf"


def draw_header(canvas: Canvas, page_number: int) -> None:
    width, height = letter
    canvas.setFillColor(HexColor("#173247"))
    canvas.rect(0, height - 56, width, 56, fill=1, stroke=0)
    canvas.setFillColor(white)
    canvas.setFont("Helvetica-Bold", 12)
    canvas.drawString(42, height - 34, "PaperBridge PDFium Fixture")
    canvas.setFont("Helvetica", 9)
    canvas.drawRightString(width - 42, height - 34, f"Page {page_number}")


def create_fixture() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    canvas = Canvas(str(OUTPUT), pagesize=letter, pageCompression=0)
    canvas.setTitle("PDFium Text Layer Fixture")
    canvas.setAuthor("PaperBridge contributors")
    canvas.setSubject("Public-domain integration test fixture")
    canvas.setCreator("PaperBridge fixture generator")
    canvas.setKeywords("PDFium, reactor physics, DOI, outline, text layer")

    width, height = letter
    canvas.bookmarkPage("page-1")
    canvas.addOutlineEntry("Reactor physics text", "page-1", level=0, closed=False)
    canvas.bookmarkPage("page-1-reference")
    canvas.addOutlineEntry("Reference parameters", "page-1-reference", level=1, closed=False)
    draw_header(canvas, 1)

    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 24)
    canvas.drawString(48, height - 108, "PDFium Text Layer Fixture")
    canvas.setFont("Helvetica", 11)
    canvas.setFillColor(HexColor("#53636E"))
    canvas.drawString(48, height - 132, "A deterministic document for text and coordinate extraction tests.")
    canvas.setFont("Helvetica", 9)
    canvas.drawString(48, height - 151, "DOI: 10.1234/paperbridge.fixture")

    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 14)
    canvas.drawString(48, height - 194, "1. Reactor physics text")
    canvas.setFont("Times-Roman", 12)
    canvas.setFillColor(HexColor("#20272C"))
    canvas.drawString(48, height - 222, "The effective multiplication factor is unity.")
    canvas.drawString(48, height - 243, "Neutron flux remains stable under the reference condition.")
    canvas.drawString(48, height - 264, "The benchmark value is k-effective = 1.0000.")

    table_top = height - 320
    table_left = 48
    table_width = width - 96
    row_height = 29
    canvas.setFillColor(HexColor("#315B78"))
    canvas.rect(table_left, table_top - row_height, table_width, row_height, fill=1, stroke=0)
    canvas.setFillColor(white)
    canvas.setFont("Helvetica-Bold", 10)
    canvas.drawString(table_left + 12, table_top - 19, "Parameter")
    canvas.drawString(table_left + 220, table_top - 19, "Value")
    canvas.drawString(table_left + 340, table_top - 19, "Unit")

    rows = [
        ("Effective multiplication factor", "1.0000", "dimensionless"),
        ("Neutron flux", "2.50E+14", "n/cm2/s"),
        ("Delayed neutron fraction", "0.0065", "dimensionless"),
    ]
    canvas.setFont("Helvetica", 10)
    for row_index, row in enumerate(rows, start=1):
        y = table_top - row_height * (row_index + 1)
        canvas.setFillColor(HexColor("#F1F4F5") if row_index % 2 else white)
        canvas.rect(table_left, y, table_width, row_height, fill=1, stroke=0)
        canvas.setFillColor(HexColor("#20272C"))
        canvas.drawString(table_left + 12, y + 10, row[0])
        canvas.drawString(table_left + 220, y + 10, row[1])
        canvas.drawString(table_left + 340, y + 10, row[2])

    canvas.setFillColor(HexColor("#6F7B83"))
    canvas.setFont("Helvetica-Oblique", 9)
    canvas.drawString(48, 42, "Generated locally. No external paper content is included.")
    canvas.showPage()

    canvas.bookmarkPage("page-2")
    canvas.addOutlineEntry("Two-column extraction fixture", "page-2", level=0, closed=False)
    draw_header(canvas, 2)
    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 18)
    canvas.drawString(48, height - 100, "2. Two-column extraction fixture")

    gutter = 24
    column_width = (width - 96 - gutter) / 2
    left_x = 48
    right_x = left_x + column_width + gutter
    column_top = height - 142
    column_height = 300

    for x, title in ((left_x, "LEFT COLUMN"), (right_x, "RIGHT COLUMN")):
        canvas.setFillColor(HexColor("#F1F4F5"))
        canvas.roundRect(x, column_top - column_height, column_width, column_height, 6, fill=1, stroke=0)
        canvas.setFillColor(HexColor("#315B78"))
        canvas.setFont("Helvetica-Bold", 11)
        canvas.drawString(x + 16, column_top - 26, title)

    canvas.setFillColor(HexColor("#20272C"))
    canvas.setFont("Times-Roman", 11)
    canvas.drawString(left_x + 16, column_top - 58, "Prompt neutrons are emitted")
    canvas.drawString(left_x + 16, column_top - 75, "immediately after fission.")
    canvas.drawString(left_x + 16, column_top - 108, "This text is drawn before the")
    canvas.drawString(left_x + 16, column_top - 125, "right-column content.")

    canvas.drawString(right_x + 16, column_top - 58, "Delayed neutron precursors")
    canvas.drawString(right_x + 16, column_top - 75, "govern reactor kinetics.")
    canvas.drawString(right_x + 16, column_top - 108, "Column order must be assessed")
    canvas.drawString(right_x + 16, column_top - 125, "before bilingual alignment.")

    canvas.setFillColor(HexColor("#173247"))
    canvas.setFont("Helvetica-Bold", 12)
    canvas.drawString(48, 238, "Expected extraction characteristics")
    canvas.setFillColor(HexColor("#20272C"))
    canvas.setFont("Helvetica", 10)
    canvas.drawString(58, 214, "- Two pages with a normal selectable text layer")
    canvas.drawString(58, 196, "- Stable page size of 612 x 792 PDF points")
    canvas.drawString(58, 178, "- Character boxes available for visible text")

    canvas.setFillColor(HexColor("#6F7B83"))
    canvas.setFont("Helvetica-Oblique", 9)
    canvas.drawString(48, 42, "This page intentionally contains a simple two-column layout.")

    canvas.save()


if __name__ == "__main__":
    create_fixture()
