#include "TestImages.h"

#include <QByteArray>
#include <QJsonArray>

#include <algorithm>
#include <cstdint>

namespace testimages
{
	namespace
	{
		ColorRgb toColor(const QJsonValue& value)
		{
			const QJsonArray a = value.toArray();
			return ColorRgb(static_cast<uint8_t>(a[0].toInt()), static_cast<uint8_t>(a[1].toInt()), static_cast<uint8_t>(a[2].toInt()));
		}

		uint8_t mix(uint8_t from, uint8_t to, int position, int range)
		{
			return static_cast<uint8_t>(from + (to - from) * position / std::max(range, 1));
		}
	}

	Image<ColorRgb> render(const QJsonObject& spec, int width, int height)
	{
		Image<ColorRgb> image(width, height);
		const QString kind = spec["kind"].toString();

		if (kind == "overlay")
		{
			image = render(spec["base"].toObject(), width, height);
			for (const QJsonValue& item : spec["rects"].toArray())
			{
				const QJsonArray r = item.toArray();
				const ColorRgb color = toColor(r[4]);
				const int x0 = std::max(r[0].toInt(), 0);
				const int y0 = std::max(r[1].toInt(), 0);
				const int x1 = std::min(r[0].toInt() + r[2].toInt(), width);
				const int y1 = std::min(r[1].toInt() + r[3].toInt(), height);
				for (int y = y0; y < y1; y++)
					for (int x = x0; x < x1; x++)
						image(x, y) = color;
			}
			return image;
		}

		if (kind == "hex")
		{
			const QByteArray data = QByteArray::fromHex(spec["rgb"].toString().toLatin1());
			std::copy_n(reinterpret_cast<const uint8_t*>(data.constData()), std::min<qsizetype>(data.size(), width * height * 3), image.rawMem());
			return image;
		}

		uint32_t state = static_cast<uint32_t>(spec["seed"].toDouble());
		auto next = [&state]() {
			state ^= state << 13;
			state ^= state >> 17;
			state ^= state << 5;
			return static_cast<uint8_t>(state >> 24);
		};

		const ColorRgb from = toColor(spec["from"]);
		const ColorRgb to = toColor(spec["to"]);
		const QJsonArray colors = spec["colors"].toArray();
		const int cell = std::max(spec["cell"].toInt(1), 1);

		for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++)
			{
				ColorRgb& pixel = image(x, y);
				if (kind == "solid")
					pixel = toColor(spec["color"]);
				else if (kind == "hgradient")
					pixel = ColorRgb(mix(from.red, to.red, x, width - 1), mix(from.green, to.green, x, width - 1), mix(from.blue, to.blue, x, width - 1));
				else if (kind == "vgradient")
					pixel = ColorRgb(mix(from.red, to.red, y, height - 1), mix(from.green, to.green, y, height - 1), mix(from.blue, to.blue, y, height - 1));
				else if (kind == "quadrants")
					pixel = toColor(colors[((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1)]);
				else if (kind == "checker")
					pixel = toColor(colors[((x / cell + y / cell) % 2 == 0) ? 0 : 1]);
				else if (kind == "random")
				{
					pixel.red = next();
					pixel.green = next();
					pixel.blue = next();
				}
			}
		return image;
	}

	QJsonArray rgb(int r, int g, int b)
	{
		return QJsonArray{ r, g, b };
	}

	QJsonObject solid(int r, int g, int b)
	{
		return QJsonObject{ { "kind", "solid" }, { "color", rgb(r, g, b) } };
	}

	QJsonObject horizontalGradient(const QJsonArray& from, const QJsonArray& to)
	{
		return QJsonObject{ { "kind", "hgradient" }, { "from", from }, { "to", to } };
	}

	QJsonObject verticalGradient(const QJsonArray& from, const QJsonArray& to)
	{
		return QJsonObject{ { "kind", "vgradient" }, { "from", from }, { "to", to } };
	}

	QJsonObject quadrants(const QJsonArray& topLeft, const QJsonArray& topRight, const QJsonArray& bottomLeft, const QJsonArray& bottomRight)
	{
		return QJsonObject{ { "kind", "quadrants" }, { "colors", QJsonArray{ topLeft, topRight, bottomLeft, bottomRight } } };
	}

	QJsonObject checker(int cell, const QJsonArray& a, const QJsonArray& b)
	{
		return QJsonObject{ { "kind", "checker" }, { "cell", cell }, { "colors", QJsonArray{ a, b } } };
	}

	QJsonObject random(unsigned seed)
	{
		return QJsonObject{ { "kind", "random" }, { "seed", static_cast<double>(seed) } };
	}

	QJsonObject overlay(const QJsonObject& base, std::initializer_list<QJsonArray> rects)
	{
		QJsonArray list;
		for (const QJsonArray& r : rects)
			list.append(r);
		return QJsonObject{ { "kind", "overlay" }, { "base", base }, { "rects", list } };
	}

	QJsonArray rect(int x, int y, int width, int height, const QJsonArray& color)
	{
		return QJsonArray{ x, y, width, height, color };
	}
}
