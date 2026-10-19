// Golden fixtures for the black border detector (overglow::BlackBorderDetector), all four detection modes.

#include <QJsonArray>
#include <QJsonObject>
#include <QTest>

#include <blackborder/BlackBorderDetector.h>

#include "GoldenFixture.h"
#include "TestImages.h"

using golden::JsonNode;
using namespace testimages;

namespace
{
	JsonNode border(const overglow::BlackBorder& b)
	{
		return JsonNode::object().set("unknown", b.unknown).set("horizontalSize", b.horizontalSize).set("verticalSize", b.verticalSize);
	}

	struct Picture
	{
		QString name;
		QJsonObject spec;
	};

	// bars of the given color around a random picture
	QJsonObject bars(int width, int height, int top, int bottom, int left, int right, const QJsonArray& color = rgb(0, 0, 0), unsigned seed = 1)
	{
		return overlay(random(seed), {
			rect(0, 0, width, top, color), rect(0, height - bottom, width, bottom, color),
			rect(0, 0, left, height, color), rect(width - right, 0, right, height, color) });
	}

	std::vector<Picture> pictures(int w, int h)
	{
		const QJsonArray black = rgb(0, 0, 0);
		const QJsonArray white = rgb(255, 255, 255);
		return {
			{ "none", random(1) },
			{ "letterbox", bars(w, h, h / 8, h / 8, 0, 0) },
			{ "pillarbox", bars(w, h, 0, 0, w / 8, w / 8) },
			{ "windowbox", bars(w, h, h / 10, h / 10, w / 9, w / 9) },
			{ "letterbox-near-black", bars(w, h, h / 8, h / 8, 0, 0, rgb(10, 10, 10)) },
			{ "letterbox-dark-gray", bars(w, h, h / 8, h / 8, 0, 0, rgb(30, 30, 30)) },
			{ "letterbox-asymmetric", bars(w, h, h / 7, h / 20, 0, 0) },
			{ "letterbox-subtitle", overlay(bars(w, h, h / 6, h / 6, 0, 0), { rect(w / 2 - w / 6, h - h / 10, w / 3, h / 30 + 1, white) }) },
			{ "letterbox-logo-top-left", overlay(bars(w, h, h / 6, h / 6, 0, 0), { rect(w / 4 - 2, 1, 5, 3, white) }) },
			{ "pillarbox-osd-left", overlay(bars(w, h, 0, 0, w / 7, w / 7), { rect(0, h / 3 - 2, w / 14, 5, white) }) },
			{ "black", solid(0, 0, 0) },
			{ "black-with-dot", overlay(solid(0, 0, 0), { rect(w / 2, h / 2, 1, 1, white) }) },
			{ "dark-content", solid(12, 12, 12) },
			{ "bright-corner", overlay(bars(w, h, h / 8, h / 8, w / 8, w / 8), { rect(0, 0, 2, 2, white) }) },
			{ "gradient", horizontalGradient(black, white) },
		};
	}
}

class BlackBorderGolden : public QObject
{
	Q_OBJECT

private slots:
	void initTestCase()
	{
		Logger::getInstance()->setLogLevel(Logger::WARNING);
	}

	void detector()
	{
		const double thresholds[] = { 0.05, 0.03, 0.15 };
		const struct { int width; int height; } sizes[] = { { 160, 90 }, { 101, 57 }, { 9, 7 } };

		JsonNode cases = JsonNode::array();
		for (const auto& size : sizes)
			for (const Picture& picture : pictures(size.width, size.height))
			{
				const Image<ColorRgb> image = render(picture.spec, size.width, size.height);
				JsonNode results = JsonNode::array();
				for (const double threshold : thresholds)
				{
					const overglow::BlackBorderDetector detector(threshold);
					results.add(JsonNode::object()
						.set("threshold", threshold)
						.set("default", border(detector.process(image)))
						.set("classic", border(detector.process_classic(image)))
						.set("osd", border(detector.process_osd(image)))
						.set("letterbox", border(detector.process_letterbox(image))));
				}
				cases.add(JsonNode::object()
					.set("name", picture.name)
					.set("image", golden::fromQt(picture.spec))
					.set("width", size.width)
					.set("height", size.height)
					.set("fnv1a", golden::fnv1a(image.rawMem(), image.size()))
					.set("results", results));
			}

		JsonNode thresholdBytes = JsonNode::array();
		for (const double threshold : { -0.1, 0.0, 0.01, 0.03, 0.05, 0.1, 0.15, 0.5, 1.0, 1.5 })
			thresholdBytes.add(JsonNode::array().add(threshold).add(static_cast<int>(overglow::BlackBorderDetector::calculateThreshold(threshold))));

		const JsonNode document = JsonNode::object()
			.set("source", "C++ reference: overglow::BlackBorderDetector (tests/golden/BlackBorderGolden.cpp)")
			.set("description", "Detected border per image, threshold (the setting / 100) and mode; calculateThreshold maps the fraction to the 8-bit limit (a pixel is black when all channels are below it).")
			.set("calculateThreshold", thresholdBytes)
			.set("cases", cases);
		GOLDEN_CHECK("blackborder/cpp-detector.json", document);
	}
};

QTEST_GUILESS_MAIN(BlackBorderGolden)
#include "BlackBorderGolden.moc"
