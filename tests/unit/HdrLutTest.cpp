// Characterization tests for the HDR tone mapping table of the system grabber: the generated default table and the loader.

#include <QCryptographicHash>
#include <QFile>
#include <QTemporaryDir>
#include <QTest>

#include <utils/DefaultLut.h>
#include <utils/LutLoader.h>
#include <image/MemoryBuffer.h>

namespace
{
	// MD5 of the HDR RGB table that HyperHDR v22 created as its default LUT (the first table of the owner's lut_lin_tables.3d)
	const QByteArray DEFAULT_TABLE_MD5 = "51da2e6ff577303ec3b010f9d296f2d6";

	QByteArray defaultTable()
	{
		MemoryBuffer<uint8_t> table(LutLoader::TABLE_SIZE);
		DefaultLut::fillTable(table.data());
		return QByteArray(reinterpret_cast<const char*>(table.data()), LutLoader::TABLE_SIZE);
	}

	QByteArray md5(const QByteArray& data)
	{
		return QCryptographicHash::hash(data, QCryptographicHash::Md5).toHex();
	}

	bool writeFile(const QString& fileName, const QByteArray& data)
	{
		QFile file(fileName);
		return file.open(QIODevice::WriteOnly) && file.write(data) == data.size();
	}
}

class HdrLutTest : public QObject
{
	Q_OBJECT

	QByteArray _table;

private slots:
	void initTestCase()
	{
		_table = defaultTable();
	}

	void defaultTableMatchesLegacyDefault()
	{
		QCOMPARE(md5(_table), DEFAULT_TABLE_MD5);
	}

	void defaultTableMapsSamples()
	{
		auto at = [this](int r, int g, int b) {
			const char* entry = _table.constData() + LUT_INDEX(uint32_t(r), uint32_t(g), uint32_t(b));
			return QList<int>{ uint8_t(entry[0]), uint8_t(entry[1]), uint8_t(entry[2]) };
		};

		// PQ encoded grays: dark levels are compressed, the SDR white is reached at about 60% of the signal
		QCOMPARE(at(0, 0, 0), (QList<int>{ 0, 0, 0 }));
		QCOMPARE(at(32, 32, 32), (QList<int>{ 9, 9, 9 }));
		QCOMPARE(at(64, 64, 64), (QList<int>{ 43, 43, 43 }));
		QCOMPARE(at(128, 128, 128), (QList<int>{ 176, 176, 176 }));
		QCOMPARE(at(160, 160, 160), (QList<int>{ 255, 255, 255 }));
		QCOMPARE(at(200, 0, 0), (QList<int>{ 255, 0, 0 }));
	}

	void createFileWritesOneTable()
	{
		QTemporaryDir dir;
		QVERIFY(dir.isValid());
		QVERIFY(DefaultLut::createFile(dir.path()).isEmpty());

		QFile file(dir.filePath("lut_lin_tables.3d"));
		QVERIFY(file.open(QIODevice::ReadOnly));
		QCOMPARE(md5(file.readAll()), DEFAULT_TABLE_MD5);
		QVERIFY(!QFile::exists(dir.filePath("lut_lin_tables.3d.tmp")));
	}

	void loaderAcceptsOneAndThreeTableFiles()
	{
		QTemporaryDir dir;
		QVERIFY(dir.isValid());
		const QString single = dir.filePath("single.3d");
		const QString legacy = dir.filePath("legacy.3d");
		QVERIFY(writeFile(single, _table));
		QVERIFY(writeFile(legacy, _table + QByteArray(LutLoader::TABLE_SIZE * 2, '\x7f')));

		for (const QString& fileName : { single, legacy })
		{
			LutLoader loader;
			QVERIFY(loader.load(QString(), { dir.filePath("missing.3d"), fileName }));
			QVERIFY(loader.isLoaded());
			QCOMPARE(md5(QByteArray(reinterpret_cast<const char*>(loader.data()), LutLoader::TABLE_SIZE)), DEFAULT_TABLE_MD5);
		}
	}

	void loaderRejectsInvalidFiles()
	{
		QTemporaryDir dir;
		QVERIFY(dir.isValid());
		const QString truncated = dir.filePath("truncated.3d");
		QVERIFY(writeFile(truncated, _table.left(1000)));

		LutLoader loader;
		QVERIFY(!loader.load(QString(), { truncated }));
		QVERIFY(!loader.load(QString(), { dir.filePath("missing.3d") }));
		QVERIFY(!loader.isLoaded());
		QCOMPARE(loader.data(), nullptr);
	}
};

QTEST_GUILESS_MAIN(HdrLutTest)
#include "HdrLutTest.moc"
