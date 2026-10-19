#pragma once

// Synthetic test images described by small JSON specs. The golden fixtures store the spec (and an FNV-1a checksum of
// the pixels) instead of the pixels; the C# tests rebuild the image from the spec with the same integer math
// (backend/tests/Overglow.Core.Tests/Golden/TestImages.cs) and check the checksum first.
//
// Spec kinds ("kind"), colors are [r, g, b]:
//   solid      color
//   hgradient  from, to: column x gets from + (to - from) * x / (width - 1) per channel (integer division)
//   vgradient  from, to: the same along the rows
//   quadrants  colors: [top left, top right, bottom left, bottom right]; the left half is x < width / 2, the top half y < height / 2
//   checker    cell, colors: [a, b]; a where (x / cell + y / cell) is even
//   random     seed: xorshift32 (x ^= x << 13; x ^= x >> 17; x ^= x << 5), three outputs per pixel (r, g, b), each >> 24
//   overlay    base (a spec), rects: [[x, y, width, height, [r, g, b]], ...] painted in order and clipped to the image
//   hex        rgb: row-major RGB24 as hex

#include <QJsonArray>
#include <QJsonObject>

#include <initializer_list>

#include <image/Image.h>
#include <image/ColorRgb.h>

namespace testimages
{
	Image<ColorRgb> render(const QJsonObject& spec, int width, int height);

	QJsonObject solid(int r, int g, int b);
	QJsonObject horizontalGradient(const QJsonArray& from, const QJsonArray& to);
	QJsonObject verticalGradient(const QJsonArray& from, const QJsonArray& to);
	QJsonObject quadrants(const QJsonArray& topLeft, const QJsonArray& topRight, const QJsonArray& bottomLeft, const QJsonArray& bottomRight);
	QJsonObject checker(int cell, const QJsonArray& a, const QJsonArray& b);
	QJsonObject random(unsigned seed);
	QJsonObject overlay(const QJsonObject& base, std::initializer_list<QJsonArray> rects);
	QJsonArray rect(int x, int y, int width, int height, const QJsonArray& color);
	QJsonArray rgb(int r, int g, int b);
}
