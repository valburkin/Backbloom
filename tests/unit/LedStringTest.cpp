// Characterization tests for LedString: parsing of the `leds` setting and the LED layout grid size.

#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSize>
#include <QTest>

#include <base/LedString.h>

namespace
{
	QJsonArray loadFixture(const QString& name)
	{
		QFile file(QStringLiteral(OVERGLOW_TEST_FIXTURES_DIR "/") + name);
		if (!file.open(QIODevice::ReadOnly))
			return {};
		return QJsonDocument::fromJson(file.readAll()).array();
	}

	QJsonObject led(double hmin, double hmax, double vmin, double vmax)
	{
		return QJsonObject{ {"hmin", hmin}, {"hmax", hmax}, {"vmin", vmin}, {"vmax", vmax} };
	}
}

class LedStringTest : public QObject
{
	Q_OBJECT

private slots:
	void ownerLayoutParses()
	{
		const QJsonArray config = loadFixture("leds-168.json");
		QCOMPARE(config.size(), 168);

		const LedString ledString = LedString::createLedString(config, LedString::ColorOrder::ORDER_RGB);
		const auto& leds = ledString.leds();
		QCOMPARE(leds.size(), size_t(168));
		QVERIFY(!ledString.hasDisabled);

		for (const auto& item : leds)
		{
			QVERIFY(item.minX_frac >= 0.0 && item.minX_frac <= item.maxX_frac && item.maxX_frac <= 1.0);
			QVERIFY(item.minY_frac >= 0.0 && item.minY_frac <= item.maxY_frac && item.maxY_frac <= 1.0);
			QCOMPARE(item.group, 0);
			QVERIFY(!item.disabled);
		}

		QCOMPARE(leds.front().minX_frac, 0.9694);
		QCOMPARE(leds.front().maxX_frac, 1.0);
		QCOMPARE(leds.front().minY_frac, 0.8);
		QCOMPARE(leds.front().maxY_frac, 1.0);
		QCOMPARE(leds.back().minX_frac, 0.0);
		QCOMPARE(leds.back().maxX_frac, 0.039);
		QCOMPARE(leds.back().minY_frac, 0.0);
		QCOMPARE(leds.back().maxY_frac, 0.25);
	}

	void ownerLayoutGridSize()
	{
		// 122 distinct horizontal and 11 vertical mid points: the height is raised to half the width,
		// then the grid is scaled down to fit 80x80.
		QCOMPARE(LedString::getLedLayoutGridSize(loadFixture("leds-168.json")), QSize(80, 40));
	}

	void swappedAndOutOfRangeValuesAreFixed()
	{
		QJsonObject item = led(0.75, 0.25, 1.5, -0.5);
		item["group"] = 3;
		item["disabled"] = true;

		const LedString ledString = LedString::createLedString(QJsonArray{ item }, LedString::ColorOrder::ORDER_RGB);
		QCOMPARE(ledString.leds().size(), size_t(1));
		const auto& parsed = ledString.leds().front();
		QCOMPARE(parsed.minX_frac, 0.25);
		QCOMPARE(parsed.maxX_frac, 0.75);
		QCOMPARE(parsed.minY_frac, 0.0);
		QCOMPARE(parsed.maxY_frac, 1.0);
		QCOMPARE(parsed.group, 3);
		QVERIFY(parsed.disabled);
		QVERIFY(ledString.hasDisabled);
	}

	void gridSize_data()
	{
		QTest::addColumn<QJsonArray>("leds");
		QTest::addColumn<QSize>("expected");

		QTest::newRow("empty") << QJsonArray{} << QSize(0, 0);
		QTest::newRow("single") << QJsonArray{ led(0, 1, 0, 1) } << QSize(1, 1);
		QTest::newRow("row of four") << QJsonArray{ led(0, 0.25, 0, 0.1), led(0.25, 0.5, 0, 0.1), led(0.5, 0.75, 0, 0.1), led(0.75, 1, 0, 0.1) }
			<< QSize(4, 2);
		QTest::newRow("column of four") << QJsonArray{ led(0, 0.1, 0, 0.25), led(0, 0.1, 0.25, 0.5), led(0, 0.1, 0.5, 0.75), led(0, 0.1, 0.75, 1) }
			<< QSize(2, 4);
		QTest::newRow("duplicate mid points") << QJsonArray{ led(0, 0.5, 0, 0.5), led(0, 0.5, 0, 0.5), led(0.5, 1, 0.5, 1) } << QSize(2, 2);
	}

	void gridSize()
	{
		QFETCH(QJsonArray, leds);
		QFETCH(QSize, expected);
		QCOMPARE(LedString::getLedLayoutGridSize(leds), expected);
	}

	void colorOrder_data()
	{
		QTest::addColumn<QString>("name");
		QTest::addColumn<LedString::ColorOrder>("order");

		QTest::newRow("rgb") << "rgb" << LedString::ColorOrder::ORDER_RGB;
		QTest::newRow("rbg") << "rbg" << LedString::ColorOrder::ORDER_RBG;
		QTest::newRow("grb") << "grb" << LedString::ColorOrder::ORDER_GRB;
		QTest::newRow("brg") << "brg" << LedString::ColorOrder::ORDER_BRG;
		QTest::newRow("gbr") << "gbr" << LedString::ColorOrder::ORDER_GBR;
		QTest::newRow("bgr") << "bgr" << LedString::ColorOrder::ORDER_BGR;
	}

	void colorOrder()
	{
		QFETCH(QString, name);
		QFETCH(LedString::ColorOrder, order);
		QCOMPARE(LedString::stringToColorOrder(name), order);
		QCOMPARE(LedString::colorOrderToString(order), name);
		QCOMPARE(LedString::createColorOrder(QJsonObject{ {"colorOrder", name} }), order);
	}

	void colorOrderDefaultsToRgb()
	{
		QCOMPARE(LedString::createColorOrder(QJsonObject{}), LedString::ColorOrder::ORDER_RGB);
	}
};

QTEST_GUILESS_MAIN(LedStringTest)

#include "LedStringTest.moc"
