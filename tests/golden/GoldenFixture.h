#pragma once

// Golden fixtures for the .NET backend: JSON files under backend/tests/fixtures that record what the C++ reference
// code computes for deterministic inputs. The C# tests load them and must reproduce the values.
//
// By default a golden test compares the JSON it generates with the committed file (so ctest fails when the reference
// behavior changes); with the environment variable OVERGLOW_UPDATE_FIXTURES=1 it rewrites the files instead.

#include <QByteArray>
#include <QJsonValue>
#include <QString>

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

#include <linalg.h>
#include <utils/Logger.h>

namespace golden
{
	// An ordered JSON value. Floats and doubles are kept apart so each is written with the shortest text that reads
	// back to the same value in its own precision (C# reads float fixtures with float parsing).
	class JsonNode
	{
	public:
		enum class Kind { Null, Bool, Integer, Float, Double, String, Array, Object };

		JsonNode() = default;
		JsonNode(bool value);
		JsonNode(int value);
		JsonNode(unsigned value);
		JsonNode(long long value);
		JsonNode(float value);
		JsonNode(double value);
		JsonNode(const char* value);
		JsonNode(const std::string& value);
		JsonNode(const QString& value);

		static JsonNode array();
		static JsonNode object();

		// appends to an array
		JsonNode& add(JsonNode value);
		// sets a member of an object (members keep their insertion order)
		JsonNode& set(const std::string& key, JsonNode value);

		// "compact but readable": containers that fit on one line are written on one line, others one member per line
		std::string dump() const;

	private:
		void writeCompact(std::string& out) const;
		void writePretty(std::string& out, int indent) const;

		Kind _kind = Kind::Null;
		bool _bool = false;
		long long _integer = 0;
		double _number = 0;
		std::string _string;
		std::vector<JsonNode> _items;
		std::vector<std::pair<std::string, JsonNode>> _members;
	};

	JsonNode fromQt(const QJsonValue& value);
	JsonNode vec(const linalg::aliases::float3& value);
	JsonNode vecs(const std::vector<linalg::aliases::float3>& values);

	QString hex(const uint8_t* data, size_t size);
	QString base64(const uint8_t* data, size_t size);
	// FNV-1a (32 bit) as 8 lowercase hex digits; lets the C# side check that it rebuilt an input image exactly
	QString fnv1a(const uint8_t* data, size_t size);

	// Reads a JSON file of the C++ test fixtures (tests/fixtures).
	QJsonValue readTestFixture(const QString& relativePath);

	// Compares (or with OVERGLOW_UPDATE_FIXTURES=1 writes) backend/tests/fixtures/<relativePath>.
	// Returns an empty string on success, else the reason.
	QString checkFixture(const QString& relativePath, const JsonNode& document);
}

// Fails the current Qt test when the fixture differs from the generated document.
#define GOLDEN_CHECK(relativePath, document) \
	do { const QString goldenError = golden::checkFixture((relativePath), (document)); \
		if (!goldenError.isEmpty()) QFAIL(qPrintable(goldenError)); } while (false)
