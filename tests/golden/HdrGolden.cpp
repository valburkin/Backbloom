// Golden fixtures for the HDR tone mapping of the system grabber: the default LUT (DefaultLut), the LUT file format
// (LutLoader) and the BGRA decoder that applies the table to captured frames (decodeBGRA).

#include <QCryptographicHash>
#include <QFile>
#include <QJsonArray>
#include <QJsonObject>
#include <QTemporaryDir>
#include <QTest>

#include <grabber/windows/DX/BgraDecoder.h>
#include <image/MemoryBuffer.h>
#include <utils/DefaultLut.h>
#include <utils/LutLoader.h>

#include "GoldenFixture.h"
#include "TestImages.h"

using golden::JsonNode;
using namespace testimages;

namespace
{
	// BGRA frame from an RGB test image; alpha varies to show that the decoder ignores it
	std::vector<uint8_t> toBgra(const Image<ColorRgb>& image, int lineSize)
	{
		std::vector<uint8_t> bgra(static_cast<size_t>(lineSize) * image.height(), 0xEE);
		for (unsigned y = 0; y < image.height(); y++)
			for (unsigned x = 0; x < image.width(); x++)
			{
				const ColorRgb& c = image(x, y);
				uint8_t* p = &bgra[y * lineSize + x * 4];
				p[0] = c.blue;
				p[1] = c.green;
				p[2] = c.red;
				p[3] = static_cast<uint8_t>((x * 31 + y * 17) & 255);
			}
		return bgra;
	}

	bool writeFile(const QString& fileName, const QByteArray& data)
	{
		QFile file(fileName);
		return file.open(QIODevice::WriteOnly) && file.write(data) == data.size();
	}
}

class HdrGolden : public QObject
{
	Q_OBJECT

	QByteArray _table;

private slots:
	void initTestCase()
	{
		Logger::getInstance()->setLogLevel(Logger::WARNING);
		MemoryBuffer<uint8_t> table(LutLoader::TABLE_SIZE);
		DefaultLut::fillTable(table.data());
		_table = QByteArray(reinterpret_cast<const char*>(table.data()), LutLoader::TABLE_SIZE);
	}

	void defaultLut()
	{
		std::vector<int> grid;
		for (int v = 0; v <= 255; v += 15)
			grid.push_back(v);

		std::vector<uint8_t> samples;
		for (int b : grid)
			for (int g : grid)
				for (int r : grid)
				{
					const char* entry = _table.constData() + LUT_INDEX(r, g, b);
					samples.insert(samples.end(), { uint8_t(entry[0]), uint8_t(entry[1]), uint8_t(entry[2]) });
				}

		std::vector<uint8_t> grays;
		for (int v = 0; v < 256; v++)
		{
			const char* entry = _table.constData() + LUT_INDEX(v, v, v);
			grays.insert(grays.end(), { uint8_t(entry[0]), uint8_t(entry[1]), uint8_t(entry[2]) });
		}

		JsonNode gridJson = JsonNode::array();
		for (int v : grid)
			gridJson.add(v);

		const JsonNode document = JsonNode::object()
			.set("source", "C++ reference: DefaultLut::fillTable, LutLoader (tests/golden/HdrGolden.cpp)")
			.set("description", "The default HDR10 (PQ, BT.2020) to SDR table of the system grabber. File 'lut_lin_tables.3d': "
				"256*256*256 RGB24 entries indexed (r + (g << 8) + (b << 16)) * 3 (red fastest); legacy files hold three such tables "
				"(HDR RGB, HDR YUV, YUV) and only the first is used; a '.zst' file holds the same bytes zstd-compressed.")
			.set("tableSize", LutLoader::TABLE_SIZE)
			.set("md5", QString::fromLatin1(QCryptographicHash::hash(_table, QCryptographicHash::Md5).toHex()))
			.set("grid", gridJson)
			.set("gridOrder", "blue outer, green, red inner; RGB24 per entry")
			.set("samples", golden::base64(samples.data(), samples.size()))
			.set("grays", golden::base64(grays.data(), grays.size()));
		GOLDEN_CHECK("hdr/cpp-default-lut.json", document);
	}

	void decodeBgra()
	{
		// load the table through the file format, the way the grabber does (legacy three-table layout)
		QTemporaryDir dir;
		QVERIFY(dir.isValid());
		const QString fileName = dir.filePath("lut_lin_tables.3d");
		QVERIFY(writeFile(fileName, _table + QByteArray(LutLoader::TABLE_SIZE * 2, '\x7f')));
		LutLoader loader;
		QVERIFY(loader.load(QString(), { fileName }));

		struct Case
		{
			QString name;
			QJsonObject image;
			int width, height, padding;
			int division, startX, startY, targetWidth, targetHeight;
			bool lut;
		};
		// target sizes follow Grabber::getTargetSystemFrameDimension: (width - crop) / division
		const std::vector<Case> cases{
			{ "copy", random(3), 96, 54, 0, 1, 0, 0, 96, 54, false },
			{ "lut", random(3), 96, 54, 0, 1, 0, 0, 96, 54, true },
			{ "lut-gradient", horizontalGradient(rgb(0, 0, 0), rgb(255, 255, 255)), 96, 54, 0, 1, 0, 0, 96, 54, true },
			{ "lut-division-2", random(4), 96, 54, 0, 2, 0, 0, 48, 27, true },
			{ "lut-crop-division-3-padded", random(5), 96, 54, 32, 3, 5, 4, 29, 16, true },
			{ "copy-crop-division-2-padded", random(5), 96, 54, 32, 2, 7, 3, 42, 24, false },
		};

		JsonNode results = JsonNode::array();
		for (const Case& c : cases)
		{
			const Image<ColorRgb> rgbImage = render(c.image, c.width, c.height);
			const int lineSize = c.width * 4 + c.padding;
			const std::vector<uint8_t> bgra = toBgra(rgbImage, lineSize);

			Image<ColorRgb> output(c.targetWidth, c.targetHeight);
			decodeBGRA(output, c.targetWidth, c.targetHeight, c.startX, c.startY, bgra.data(), c.width, c.height, c.division,
				(c.lut) ? loader.data() : nullptr, (c.padding > 0) ? lineSize : 0);

			results.add(JsonNode::object()
				.set("name", c.name)
				.set("image", golden::fromQt(c.image))
				.set("width", c.width)
				.set("height", c.height)
				.set("lineSize", lineSize)
				.set("bgraFnv1a", golden::fnv1a(bgra.data(), bgra.size()))
				.set("lut", (c.lut) ? "default" : "none")
				.set("division", c.division)
				.set("startX", c.startX)
				.set("startY", c.startY)
				.set("targetWidth", c.targetWidth)
				.set("targetHeight", c.targetHeight)
				.set("expectedRgb", golden::base64(output.rawMem(), static_cast<size_t>(c.targetWidth) * c.targetHeight * 3)));
		}

		const JsonNode document = JsonNode::object()
			.set("source", "C++ reference: decodeBGRA of the system grabber with the default LUT loaded by LutLoader (tests/golden/HdrGolden.cpp)")
			.set("description", "BGRA input: the RGB test image as B, G, R, A with A = (x * 31 + y * 17) & 255 and row padding bytes 0xEE; "
				"output: RGB24 of targetWidth x targetHeight, pixel (i, j) taken from source (startX + i * division, min(startY + j * division, height - 1)).")
			.set("cases", results);
		GOLDEN_CHECK("hdr/cpp-decode-bgra.json", document);
	}
};

QTEST_GUILESS_MAIN(HdrGolden)
#include "HdrGolden.moc"
