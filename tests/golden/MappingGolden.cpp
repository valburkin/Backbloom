// Golden fixtures for the LED layout (LedString) and the image to LED mapping (ImageColorAveraging, the code
// ImageToLedManager runs per frame), consumed by backend/tests/Overglow.Core.Tests.

#include <QJsonArray>
#include <QJsonObject>
#include <QTest>

#include <base/ImageColorAveraging.h>
#include <base/LedString.h>

#include "GoldenFixture.h"
#include "TestImages.h"

using golden::JsonNode;
using namespace linalg::aliases;
using namespace testimages;

namespace
{
	struct Resolution
	{
		int width;
		int height;
	};

	struct Variant
	{
		QJsonObject image;
		QString mapping;
		bool sparse;
		int horizontalBorder;
		int verticalBorder;
	};

	QJsonObject area(double hmin, double hmax, double vmin, double vmax, int group = 0)
	{
		return QJsonObject{ { "hmin", hmin }, { "hmax", hmax }, { "vmin", vmin }, { "vmax", vmax }, { "group", group } };
	}

	// one LED per quadrant, the whole frame, the center and the four edge strips of a classic layout
	QJsonArray simpleLayout()
	{
		return QJsonArray{
			area(0, 0.5, 0, 0.5), area(0.5, 1, 0, 0.5), area(0, 0.5, 0.5, 1), area(0.5, 1, 0.5, 1),
			area(0, 1, 0, 1), area(0.25, 0.75, 0.25, 0.75),
			area(0, 1, 0, 0.08), area(0.92, 1, 0, 1), area(0, 1, 0.92, 1), area(0, 0.08, 0, 1) };
	}

	// 20 top, 10 right, 20 bottom, 10 left LEDs, 5% deep
	QJsonArray classicLayout()
	{
		QJsonArray leds;
		for (int i = 0; i < 20; i++)
			leds.append(area(i / 20.0, (i + 1) / 20.0, 0, 0.05));
		for (int i = 0; i < 10; i++)
			leds.append(area(0.95, 1, i / 10.0, (i + 1) / 10.0));
		for (int i = 19; i >= 0; i--)
			leds.append(area(i / 20.0, (i + 1) / 20.0, 0.95, 1));
		for (int i = 9; i >= 0; i--)
			leds.append(area(0, 0.05, i / 10.0, (i + 1) / 10.0));
		return leds;
	}

	// edge cases of LedString and ImageColorAveraging
	QJsonArray specialLayout()
	{
		QJsonObject disabled = area(0, 1, 0, 1);
		disabled["disabled"] = true;
		QJsonObject disabledGrouped = area(0.5, 1, 0.5, 1, 3);
		disabledGrouped["disabled"] = true;
		QJsonObject fractionalGroup = area(0, 0.3, 0, 0.3);
		fractionalGroup["group"] = 2.5;
		QJsonObject textValues{ { "hmin", "0.1" }, { "hmax", 0.9 }, { "vmin", 0 }, { "vmax", 1 }, { "disabled", 1 } };

		return QJsonArray{
			area(0, 0.5, 0, 1, 7), area(0.5, 1, 0, 1, 7), area(0.2, 0.4, 0.2, 0.4, 7),	// group 7: three areas averaged together
			area(0.75, 1, 0, 0.25, 4), area(0, 0.25, 0.75, 1, 4),						// group 4
			disabled, disabledGrouped, area(0.4, 0.6, 0.4, 0.6, 3),						// disabled LED, disabled group master
			area(0.5, 0.5, 0, 1), area(0.3, 0.3000005, 0, 1), area(0.3, 0.300002, 0.5, 0.500002),	// empty, empty (< 1e-6), thin
			area(0.9, 0.1, 0.8, 0.2), area(-0.5, 1.5, -1, 2),							// reversed, out of range
			area(0.99, 1, 0.99, 1), area(1, 1.2, 0, 1), area(0, 1, 1, 1.1),				// last pixel, outside right / bottom
			area(0.25, 0.75, 0.125, 0.875), area(1.0 / 3, 2.0 / 3, 1.0 / 6, 5.0 / 6),		// half-pixel edges
			fractionalGroup, area(0, 0.3, 0, 0.3, -1), textValues,						// non-integer group, negative group, wrong types
			QJsonValue(42), area(0, 1, 0, 1) };											// not an object, large area (sparse above 1600 pixels)
	}

	// more distinct LED centers than the 80x80 grid limit
	QJsonArray denseLayout()
	{
		QJsonArray leds;
		for (int i = 0; i < 200; i++)
			leds.append(area(i / 200.0, (i + 1) / 200.0, 0, 0.02));
		for (int i = 0; i < 30; i++)
			leds.append(area(0, 0.01, i / 30.0, (i + 1) / 30.0));
		return leds;
	}

	// a single row: the grid height is corrected to half the width
	QJsonArray rowLayout()
	{
		QJsonArray leds;
		for (int i = 0; i < 9; i++)
			leds.append(area(i / 9.0, (i + 1) / 9.0, 0.9, 1));
		return leds;
	}

	std::vector<Variant> variants()
	{
		const QJsonObject random1 = random(1);
		const QJsonObject checker1 = checker(1, rgb(255, 255, 255), rgb(0, 0, 0));
		const QJsonObject quads = quadrants(rgb(255, 0, 0), rgb(0, 255, 0), rgb(0, 0, 255), rgb(255, 255, 255));
		return {
			{ solid(200, 100, 10), "advanced", false, 0, 0 },
			{ horizontalGradient(rgb(0, 32, 255), rgb(255, 128, 0)), "advanced", false, 0, 0 },
			{ verticalGradient(rgb(10, 250, 30), rgb(240, 5, 120)), "advanced", false, 0, 0 },
			{ quads, "advanced", false, 0, 0 },
			{ checker(3, rgb(255, 255, 255), rgb(0, 0, 0)), "advanced", false, 0, 0 },
			{ checker1, "advanced", false, 0, 0 },
			{ random1, "advanced", false, 0, 0 },
			{ random(2), "advanced", false, 0, 0 },
			{ checker1, "advanced", true, 0, 0 },
			{ random1, "advanced", true, 0, 0 },
			{ quads, "unicolor_mean", false, 0, 0 },
			{ random1, "unicolor_mean", false, 0, 0 },
			{ checker1, "unicolor_mean", true, 0, 0 },
			{ random1, "advanced", false, 4, 0 },
			{ random1, "advanced", false, 0, 6 },
			{ random1, "advanced", false, 3, 5 },
		};
	}

	JsonNode mappingCase(const LedString& ledString, const Resolution& resolution, const Variant& variant)
	{
		const Image<ColorRgb> image = render(variant.image, resolution.width, resolution.height);
		// mapping type values of ImageToLedManager::mappingTypeToInt
		overglow::ImageColorAveraging averaging("GOLDEN", (variant.mapping == "unicolor_mean") ? 1 : 0, variant.sparse,
			resolution.width, resolution.height, variant.horizontalBorder, variant.verticalBorder, 0, ledString.leds());

		std::vector<float3> colors;
		averaging.process(colors, image);

		return JsonNode::object()
			.set("image", golden::fromQt(variant.image))
			.set("width", resolution.width)
			.set("height", resolution.height)
			.set("fnv1a", golden::fnv1a(image.rawMem(), image.size()))
			.set("mappingType", variant.mapping)
			.set("sparseProcessing", variant.sparse)
			.set("horizontalBorder", variant.horizontalBorder)
			.set("verticalBorder", variant.verticalBorder)
			.set("expected", golden::vecs(colors));
	}

	JsonNode mappingFixture(const QString& description, const QJsonArray& leds, const std::vector<Resolution>& resolutions, const std::vector<Variant>& variants)
	{
		const LedString ledString = LedString::createLedString(leds, LedString::ColorOrder::ORDER_RGB);
		JsonNode cases = JsonNode::array();
		for (const Resolution& resolution : resolutions)
			for (const Variant& variant : variants)
			{
				// ImageColorAveraging requires a picture left between the borders
				if (resolution.width > 2 * variant.verticalBorder && resolution.height > 2 * variant.horizontalBorder)
					cases.add(mappingCase(ledString, resolution, variant));
			}

		return JsonNode::object()
			.set("source", "C++ reference: overglow::ImageColorAveraging + LedString::createLedString (tests/golden/MappingGolden.cpp)")
			.set("description", description)
			.set("tolerance", 0)
			.set("leds", golden::fromQt(leds))
			.set("cases", cases);
	}

	JsonNode parsingCase(const QString& name, const QJsonArray& leds)
	{
		const LedString ledString = LedString::createLedString(leds, LedString::ColorOrder::ORDER_RGB);
		JsonNode parsed = JsonNode::array();
		for (const LedString::Led& led : ledString.leds())
			parsed.add(JsonNode::array().add(led.minX_frac).add(led.maxX_frac).add(led.minY_frac).add(led.maxY_frac).add(led.group).add(led.disabled));
		const QSize grid = LedString::getLedLayoutGridSize(leds);

		return JsonNode::object()
			.set("name", name)
			.set("leds", golden::fromQt(leds))
			.set("hasDisabled", ledString.hasDisabled)
			.set("gridSize", JsonNode::array().add(grid.width()).add(grid.height()))
			.set("parsed", parsed);
	}
}

class MappingGolden : public QObject
{
	Q_OBJECT

	QJsonArray _ownerLayout;

private slots:
	void initTestCase()
	{
		Logger::getInstance()->setLogLevel(Logger::WARNING);
		_ownerLayout = golden::readTestFixture("leds-168.json").toArray();
		QCOMPARE(_ownerLayout.size(), 168);
	}

	void layoutParsing()
	{
		JsonNode cases = JsonNode::array();
		cases.add(parsingCase("owner-168", _ownerLayout));
		cases.add(parsingCase("simple", simpleLayout()));
		cases.add(parsingCase("classic-60", classicLayout()));
		cases.add(parsingCase("special", specialLayout()));
		cases.add(parsingCase("dense-230", denseLayout()));
		cases.add(parsingCase("row-9", rowLayout()));
		cases.add(parsingCase("empty", QJsonArray()));

		const JsonNode document = JsonNode::object()
			.set("source", "C++ reference: LedString::createLedString, LedString::getLedLayoutGridSize (tests/golden/MappingGolden.cpp)")
			.set("description", "parsed: [hmin, hmax, vmin, vmax, group, disabled] per LED after clamping and swapping")
			.set("cases", cases);
		GOLDEN_CHECK("layouts/cpp-parsing.json", document);
	}

	void ownerLayout()
	{
		GOLDEN_CHECK("mapping/cpp-owner-168.json", mappingFixture("The owner's 168-LED layout.", _ownerLayout,
			{ { 64, 36 }, { 101, 57 }, { 160, 90 } }, variants()));
	}

	void ownerLayoutFullSize()
	{
		const QJsonObject random1 = random(1);
		GOLDEN_CHECK("mapping/cpp-owner-168-512x288.json", mappingFixture("The owner's 168-LED layout at the capture size of the .NET synthetic source.",
			_ownerLayout, { { 512, 288 } },
			{ { random1, "advanced", false, 0, 0 }, { random1, "advanced", true, 0, 0 },
			  { horizontalGradient(rgb(0, 32, 255), rgb(255, 128, 0)), "advanced", false, 0, 0 } }));
	}

	void simpleLayouts()
	{
		const std::vector<Resolution> resolutions{ { 64, 36 }, { 101, 57 }, { 160, 90 } };
		GOLDEN_CHECK("mapping/cpp-simple.json", mappingFixture("Quadrants, whole frame, center and edge strips.", simpleLayout(), resolutions, variants()));
		GOLDEN_CHECK("mapping/cpp-classic-60.json", mappingFixture("A classic 20/10/20/10 layout, 5% deep.", classicLayout(), resolutions, variants()));
		GOLDEN_CHECK("mapping/cpp-special.json", mappingFixture("Groups, disabled LEDs, empty/thin/reversed/out-of-range areas, wrong value types.",
			specialLayout(), { { 64, 36 }, { 101, 57 }, { 7, 5 } }, variants()));
	}
};

QTEST_GUILESS_MAIN(MappingGolden)
#include "MappingGolden.moc"
