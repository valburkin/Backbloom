#include "GoldenFixture.h"

#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>

#include <charconv>
#include <cmath>

namespace golden
{
	namespace
	{
		constexpr size_t LINE_WIDTH = 120;

		void writeString(std::string& out, const std::string& value)
		{
			out += '"';
			for (const char c : value)
			{
				switch (c)
				{
					case '"': out += "\\\""; break;
					case '\\': out += "\\\\"; break;
					case '\n': out += "\\n"; break;
					case '\r': out += "\\r"; break;
					case '\t': out += "\\t"; break;
					default:
						if (static_cast<unsigned char>(c) < 0x20)
						{
							char buffer[8];
							std::snprintf(buffer, sizeof(buffer), "\\u%04x", c);
							out += buffer;
						}
						else
							out += c;
				}
			}
			out += '"';
		}

		template<typename T>
		void writeNumber(std::string& out, T value)
		{
			if (!std::isfinite(value))
			{
				out += "null";
				return;
			}
			if (value == std::trunc(value) && std::abs(value) < 1e15)
			{
				out += std::to_string(static_cast<long long>(value));
				return;
			}
			char buffer[64];
			const auto result = std::to_chars(buffer, buffer + sizeof(buffer), value);
			out.append(buffer, result.ptr);
		}
	}

	JsonNode::JsonNode(bool value) : _kind(Kind::Bool), _bool(value) {}
	JsonNode::JsonNode(int value) : _kind(Kind::Integer), _integer(value) {}
	JsonNode::JsonNode(unsigned value) : _kind(Kind::Integer), _integer(value) {}
	JsonNode::JsonNode(long long value) : _kind(Kind::Integer), _integer(value) {}
	JsonNode::JsonNode(float value) : _kind(Kind::Float), _number(value) {}
	JsonNode::JsonNode(double value) : _kind(Kind::Double), _number(value) {}
	JsonNode::JsonNode(const char* value) : _kind(Kind::String), _string(value) {}
	JsonNode::JsonNode(const std::string& value) : _kind(Kind::String), _string(value) {}
	JsonNode::JsonNode(const QString& value) : _kind(Kind::String), _string(value.toStdString()) {}

	JsonNode JsonNode::array()
	{
		JsonNode json;
		json._kind = Kind::Array;
		return json;
	}

	JsonNode JsonNode::object()
	{
		JsonNode json;
		json._kind = Kind::Object;
		return json;
	}

	JsonNode& JsonNode::add(JsonNode value)
	{
		_items.push_back(std::move(value));
		return *this;
	}

	JsonNode& JsonNode::set(const std::string& key, JsonNode value)
	{
		for (auto& member : _members)
		{
			if (member.first == key)
			{
				member.second = std::move(value);
				return *this;
			}
		}
		_members.emplace_back(key, std::move(value));
		return *this;
	}

	std::string JsonNode::dump() const
	{
		std::string out;
		writePretty(out, 0);
		out += '\n';
		return out;
	}

	void JsonNode::writeCompact(std::string& out) const
	{
		switch (_kind)
		{
			case Kind::Null: out += "null"; break;
			case Kind::Bool: out += (_bool) ? "true" : "false"; break;
			case Kind::Integer: out += std::to_string(_integer); break;
			case Kind::Float: writeNumber(out, static_cast<float>(_number)); break;
			case Kind::Double: writeNumber(out, _number); break;
			case Kind::String: writeString(out, _string); break;
			case Kind::Array:
				out += '[';
				for (size_t i = 0; i < _items.size(); i++)
				{
					if (i > 0)
						out += ", ";
					_items[i].writeCompact(out);
				}
				out += ']';
				break;
			case Kind::Object:
				out += '{';
				for (size_t i = 0; i < _members.size(); i++)
				{
					if (i > 0)
						out += ", ";
					writeString(out, _members[i].first);
					out += ": ";
					_members[i].second.writeCompact(out);
				}
				out += '}';
				break;
		}
	}

	void JsonNode::writePretty(std::string& out, int indent) const
	{
		std::string compact;
		writeCompact(compact);
		const bool container = (_kind == Kind::Array && !_items.empty()) || (_kind == Kind::Object && !_members.empty());
		if (!container || compact.size() + indent <= LINE_WIDTH)
		{
			out += compact;
			return;
		}

		const std::string inner(indent + 2, ' ');
		out += (_kind == Kind::Array) ? "[\n" : "{\n";
		const size_t count = (_kind == Kind::Array) ? _items.size() : _members.size();
		for (size_t i = 0; i < count; i++)
		{
			out += inner;
			if (_kind == Kind::Array)
				_items[i].writePretty(out, indent + 2);
			else
			{
				writeString(out, _members[i].first);
				out += ": ";
				_members[i].second.writePretty(out, indent + 2);
			}
			out += (i + 1 < count) ? ",\n" : "\n";
		}
		out += std::string(indent, ' ');
		out += (_kind == Kind::Array) ? "]" : "}";
	}

	JsonNode fromQt(const QJsonValue& value)
	{
		switch (value.type())
		{
			case QJsonValue::Bool: return JsonNode(value.toBool());
			case QJsonValue::Double:
			{
				const double number = value.toDouble();
				if (number == std::trunc(number) && std::abs(number) < 1e15)
					return JsonNode(static_cast<long long>(number));
				return JsonNode(number);
			}
			case QJsonValue::String: return JsonNode(value.toString());
			case QJsonValue::Array:
			{
				JsonNode array = JsonNode::array();
				for (const QJsonValue& item : value.toArray())
					array.add(fromQt(item));
				return array;
			}
			case QJsonValue::Object:
			{
				JsonNode object = JsonNode::object();
				const QJsonObject source = value.toObject();
				for (auto it = source.begin(); it != source.end(); ++it)
					object.set(it.key().toStdString(), fromQt(it.value()));
				return object;
			}
			default: return JsonNode();
		}
	}

	JsonNode vec(const linalg::aliases::float3& value)
	{
		return JsonNode::array().add(value.x).add(value.y).add(value.z);
	}

	JsonNode vecs(const std::vector<linalg::aliases::float3>& values)
	{
		JsonNode array = JsonNode::array();
		for (const auto& value : values)
			array.add(vec(value));
		return array;
	}

	QString hex(const uint8_t* data, size_t size)
	{
		return QString::fromLatin1(QByteArray(reinterpret_cast<const char*>(data), static_cast<qsizetype>(size)).toHex());
	}

	QString base64(const uint8_t* data, size_t size)
	{
		return QString::fromLatin1(QByteArray(reinterpret_cast<const char*>(data), static_cast<qsizetype>(size)).toBase64());
	}

	QString fnv1a(const uint8_t* data, size_t size)
	{
		uint32_t hash = 2166136261u;
		for (size_t i = 0; i < size; i++)
		{
			hash ^= data[i];
			hash *= 16777619u;
		}
		return QString("%1").arg(hash, 8, 16, QChar('0'));
	}

	QJsonValue readTestFixture(const QString& relativePath)
	{
		QFile file(QString(OVERGLOW_TEST_FIXTURES_DIR) + "/" + relativePath);
		if (!file.open(QIODevice::ReadOnly))
			return QJsonValue();
		const QJsonDocument document = QJsonDocument::fromJson(file.readAll());
		return (document.isArray()) ? QJsonValue(document.array()) : QJsonValue(document.object());
	}

	QString checkFixture(const QString& relativePath, const JsonNode& document)
	{
		const QString fileName = QString(OVERGLOW_GOLDEN_FIXTURES_DIR) + "/" + relativePath;
		const QByteArray generated = QByteArray::fromStdString(document.dump());

		if (qEnvironmentVariable("OVERGLOW_UPDATE_FIXTURES") == "1")
		{
			QDir().mkpath(QFileInfo(fileName).absolutePath());
			QFile file(fileName);
			if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate) || file.write(generated) != generated.size())
				return QString("Could not write %1").arg(fileName);
			return QString();
		}

		QFile file(fileName);
		if (!file.open(QIODevice::ReadOnly))
			return QString("%1 is missing; run the golden tests with OVERGLOW_UPDATE_FIXTURES=1 to create it").arg(fileName);

		QByteArray committed = file.readAll();
		committed.replace("\r\n", "\n");
		if (committed == generated)
			return QString();

		const QList<QByteArray> expectedLines = committed.split('\n');
		const QList<QByteArray> actualLines = generated.split('\n');
		qsizetype line = 0;
		while (line < expectedLines.size() && line < actualLines.size() && expectedLines[line] == actualLines[line])
			line++;
		auto excerpt = [](const QList<QByteArray>& lines, qsizetype index) {
			return (index < lines.size()) ? QString::fromUtf8(lines[index].left(200)) : QString("<end of file>");
		};
		return QString("%1 differs from the C++ reference output at line %2\n  committed: %3\n  generated: %4\n"
			"If the reference behavior changed on purpose, rerun with OVERGLOW_UPDATE_FIXTURES=1.")
			.arg(fileName).arg(line + 1).arg(excerpt(expectedLines, line)).arg(excerpt(actualLines, line));
	}
}
