// Characterization tests for the content based settings migration on settings taken from the owner's legacy DB.

#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>
#include <QTest>

#include <base/SettingsMigration.h>

namespace
{
	QJsonObject loadFixture(const QString& name)
	{
		QFile file(QStringLiteral(OVERGLOW_TEST_FIXTURES_DIR "/") + name);
		if (!file.open(QIODevice::ReadOnly))
			return {};
		return QJsonDocument::fromJson(file.readAll()).object();
	}
}

class SettingsMigrationTest : public QObject
{
	Q_OBJECT

private slots:
	void legacySettingsLoseRemovedOptions()
	{
		QJsonObject config = loadFixture("legacy-settings.json");
		QVERIFY(config["systemGrabber"].toObject().contains("signalDetection"));
		QVERIFY(config["systemControl"].toObject().contains("cecControl"));
		const QJsonObject original = config;

		const QStringList changes = SettingsMigration::removeUnsupportedFeatures(config);
		QCOMPARE(changes.size(), 2);

		QJsonObject grabber = original["systemGrabber"].toObject();
		grabber.remove("signalDetection");
		QCOMPARE(config["systemGrabber"].toObject(), grabber);
		QVERIFY(grabber["hdrToneMapping"].toBool());

		QJsonObject control = original["systemControl"].toObject();
		control.remove("cecControl");
		QCOMPARE(config["systemControl"].toObject(), control);

		// kept settings are untouched: the boot effect of the owner is still available, smoothing is not migrated
		QCOMPARE(config["foregroundEffect"].toObject(), original["foregroundEffect"].toObject());
		QCOMPARE(config["backgroundEffect"].toObject(), original["backgroundEffect"].toObject());
		QCOMPARE(config["smoothing"].toObject(), original["smoothing"].toObject());
	}

	void migrationIsIdempotent()
	{
		QJsonObject config = loadFixture("legacy-settings.json");
		SettingsMigration::removeUnsupportedFeatures(config);
		const QJsonObject migrated = config;

		QVERIFY(SettingsMigration::removeUnsupportedFeatures(config).isEmpty());
		QCOMPARE(config, migrated);
	}

	void removedEffectsAreReplaced_data()
	{
		QTest::addColumn<QString>("effect");
		QTest::addColumn<QString>("replacement");

		QTest::newRow("kept") << "Rainbow swirl fast" << "Rainbow swirl fast";
		QTest::newRow("kept music") << "Music: stereo for LED strip (MULTI COLOR)" << "Music: stereo for LED strip (MULTI COLOR)";
		QTest::newRow("swirl variant") << "Double swirl" << "Rainbow swirl";
		QTest::newRow("cold blobs") << "Cold mood blobs" << "Full color mood blobs";
		QTest::newRow("red blobs") << "Red mood blobs" << "Warm mood blobs";
		QTest::newRow("strobe") << "Strobe white" << "Breath";
		QTest::newRow("cinema") << "Cinema dim lights" << "Candle";
		QTest::newRow("music color") << "Music: fullscreen pulse (RED)" << "Music: fullscreen pulse (MULTI COLOR)";
		QTest::newRow("music speed") << "Music: quatro for LED strip (MULTI COLOR FAST)" << "Music: quatro for LED strip (MULTI COLOR)";
		QTest::newRow("music waves") << "Music: pulse waves for LED strip (MULTI COLOR SLOW)" << "Music: pulse waves for LED strip (MULTI COLOR)";
		QTest::newRow("unknown") << "Something new" << "Something new";
	}

	void removedEffectsAreReplaced()
	{
		QFETCH(QString, effect);
		QFETCH(QString, replacement);

		QCOMPARE(SettingsMigration::replacementEffect(effect), replacement);

		QJsonObject config = loadFixture("legacy-settings.json");
		QJsonObject background = config["backgroundEffect"].toObject();
		background["effect"] = effect;
		config["backgroundEffect"] = background;

		SettingsMigration::removeUnsupportedFeatures(config);

		background["effect"] = replacement;
		QCOMPARE(config["backgroundEffect"].toObject(), background);
		QCOMPARE(config["foregroundEffect"].toObject()["effect"].toString(), QString("Rainbow swirl fast"));
	}
};

QTEST_GUILESS_MAIN(SettingsMigrationTest)
#include "SettingsMigrationTest.moc"
