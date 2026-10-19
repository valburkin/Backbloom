// Golden fixtures for the color pipeline: InfiniteProcessing (temperature, calibration, output scale, sRGB encoding,
// user gamma, luminance/saturation, minimal backlight, power limit, color order), the final rounding of LedDevice and
// the two RGBW encoders of the LED drivers (RGBW::rgb2rgbw and InfiniteColorEngineRgbw).

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QTest>

#include <infinite-color-engine/ColorSpace.h>
#include <infinite-color-engine/InfiniteProcessing.h>
#include <led-drivers/ColorRgbw.h>
#include <led-drivers/InfiniteColorEngineRgbw.h>

#include "GoldenFixture.h"
#include "TestImages.h"

using golden::JsonNode;
using namespace linalg::aliases;

namespace
{
	const char* SOURCE = "C++ reference: InfiniteProcessing (tests/golden/ColorGolden.cpp)";

	uint32_t xorshift(uint32_t& state)
	{
		state ^= state << 13;
		state ^= state >> 17;
		state ^= state << 5;
		return state;
	}

	// 24-bit fractions are exact floats
	float randomUnit(uint32_t& state)
	{
		return static_cast<float>(xorshift(state) >> 8) / 16777216.0f;
	}

	// a 5x5x5 grid, the edges of the sRGB and LUT segments and seeded random colors
	std::vector<float3> inputColors()
	{
		std::vector<float3> colors;
		const float steps[] = { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
		for (float r : steps)
			for (float g : steps)
				for (float b : steps)
					colors.emplace_back(r, g, b);

		const float edges[] = { 1e-4f, 0.5f / 255.0f, 1.0f / 255.0f, 0.0031308f, 0.04045f, 1.0f / 511.0f, 0.5f / 511.0f, 0.2140f, 0.999f, 0.9999999f };
		for (float e : edges)
		{
			colors.emplace_back(e, e, e);
			colors.emplace_back(e, 0.0f, 1.0f - e);
		}

		uint32_t state = 12345;
		for (int i = 0; i < 40; i++)
		{
			const float r = randomUnit(state);
			const float g = randomUnit(state);
			const float b = randomUnit(state);
			colors.emplace_back(r, g, b);
		}
		return colors;
	}

	std::vector<float3> dimColors()
	{
		std::vector<float3> colors;
		uint32_t state = 777;
		for (int i = 0; i < 40; i++)
		{
			const float r = randomUnit(state) * 0.1f;
			const float g = randomUnit(state) * 0.1f;
			const float b = randomUnit(state) * 0.1f;
			colors.emplace_back(r, g, b);
		}
		return colors;
	}

	QString bytes(const std::vector<float3>& colors)
	{
		std::vector<uint8_t> data;
		for (const float3& c : colors)
		{
			const byte3 b = ColorSpaceMath::round_to_0_255<byte3>(c * 255.0f);
			data.push_back(b.x);
			data.push_back(b.y);
			data.push_back(b.z);
		}
		return golden::hex(data.data(), data.size());
	}

	struct Config
	{
		QString name;
		QString description;
		QJsonObject adjustment;
		bool dim = false;
		bool enabled = true;
	};

	QJsonObject with(QJsonObject base, std::initializer_list<std::pair<QString, QJsonValue>> changes)
	{
		for (const auto& change : changes)
			base[change.first] = change.second;
		return base;
	}

	JsonNode processFixture(const Config& config, const QString& colorOrder = "rgb")
	{
		InfiniteProcessing processing;
		processing.handleSignalInstanceSettingsChanged(settings::type::DEVICE, QJsonDocument(QJsonObject{ { "colorOrder", colorOrder } }));
		processing.updateCurrentConfig(config.adjustment);
		processing.setProcessingEnabled(config.enabled);

		const std::vector<float3> inputs = (config.dim) ? dimColors() : inputColors();
		std::vector<float3> outputs = inputs;
		processing.applyyAllProcessingSteps(outputs);

		return JsonNode::object()
			.set("source", SOURCE)
			.set("description", config.description)
			.set("step", "process")
			.set("enabled", config.enabled)
			.set("colorOrder", colorOrder)
			.set("adjustment", golden::fromQt(config.adjustment))
			.set("tolerance", 0)
			.set("inputs", golden::vecs(inputs))
			.set("expected", golden::vecs(outputs))
			.set("expectedBytes", bytes(outputs));
	}

	JsonNode calibrateFixture(const Config& config)
	{
		InfiniteProcessing processing;
		processing.updateCurrentConfig(config.adjustment);
		const auto snapshot = processing.getColorspaceCalibrationSnapshot();

		const std::vector<float3> inputs = inputColors();
		std::vector<float3> outputs = inputs;
		for (float3& color : outputs)
			processing.calibrateColorInColorspace(snapshot, color);

		return JsonNode::object()
			.set("source", "C++ reference: InfiniteProcessing::calibrateColorInColorspace (tests/golden/ColorGolden.cpp)")
			.set("description", config.description)
			.set("step", "calibrate")
			.set("adjustment", golden::fromQt(config.adjustment))
			.set("tolerance", 0)
			.set("inputs", golden::vecs(inputs))
			.set("expected", golden::vecs(outputs));
	}
}

class ColorGolden : public QObject
{
	Q_OBJECT

	QJsonObject _owner;

private slots:
	void initTestCase()
	{
		Logger::getInstance()->setLogLevel(Logger::WARNING);
		_owner = golden::readTestFixture("owner-color.json").toObject()["channelAdjustment"].toArray().first().toObject();
		QVERIFY(!_owner.isEmpty());
	}

	void process()
	{
		const QJsonArray asymmetricRed{ 250, 10, 0 };
		const QJsonArray asymmetricGreen{ 20, 240, 10 };
		const QJsonArray asymmetricBlue{ 0, 30, 220 };
		const QJsonObject matrix = with(_owner, { { "red", asymmetricRed }, { "green", asymmetricGreen }, { "blue", asymmetricBlue } });

		const std::vector<Config> configs{
			{ "owner", "The owner's color settings.", _owner },
			{ "owner-disabled", "The owner's settings with processing switched off (sRGB encoding only).", _owner, false, false },
			{ "gamma-1", "No user gamma.", with(_owner, { { "gamma", 1.0 } }) },
			{ "gamma-2.2", "User gamma 2.2.", with(_owner, { { "gamma", 2.2 } }) },
			{ "gamma-0.8", "User gamma 0.8.", with(_owner, { { "gamma", 0.8 } }) },
			{ "scale-0.5", "Brightness (scaleOutput) 0.5.", with(_owner, { { "scaleOutput", 0.5 } }) },
			{ "scale-1.6", "Brightness (scaleOutput) 1.6.", with(_owner, { { "scaleOutput", 1.6 } }) },
			{ "luminance-1.3", "Luminance gain 1.3.", with(_owner, { { "luminanceGain", 1.3 } }) },
			{ "saturation-1.5", "Saturation gain 1.5.", with(_owner, { { "saturationGain", 1.5 } }) },
			{ "luminance-0.7-saturation-0.6", "Luminance gain 0.7, saturation gain 0.6.", with(_owner, { { "luminanceGain", 0.7 }, { "saturationGain", 0.6 } }) },
			{ "temperature-warm", "Warm temperature preset.", with(_owner, { { "temperatureSetting", "warm" } }) },
			{ "temperature-cold", "Cold temperature preset.", with(_owner, { { "temperatureSetting", "cold" } }) },
			{ "temperature-neutral", "Neutral temperature preset.", with(_owner, { { "temperatureSetting", "neutral" } }) },
			{ "temperature-custom", "Custom temperature (1, 0.8, 0.6).", with(_owner, { { "temperatureSetting", "custom" }, { "temperatureRed", 1.0 }, { "temperatureGreen", 0.8 }, { "temperatureBlue", 0.6 } }) },
			{ "calibration-matrix", "Primaries calibration (classic_config) with asymmetric targets.", matrix },
			{ "calibration-lut-primaries", "The same primaries in the 3D LUT mode (classic_config false, other corners ideal).", with(matrix, { { "classic_config", false } }) },
			{ "calibration-lut-full", "3D LUT with all eight corners calibrated.", with(_owner, { { "classic_config", false },
				{ "black", QJsonArray{ 10, 20, 15 } }, { "white", QJsonArray{ 240, 235, 245 } }, { "red", QJsonArray{ 220, 50, 60 } },
				{ "green", QJsonArray{ 60, 210, 50 } }, { "blue", QJsonArray{ 50, 60, 220 } }, { "cyan", QJsonArray{ 50, 220, 215 } },
				{ "magenta", QJsonArray{ 220, 60, 220 } }, { "yellow", QJsonArray{ 220, 210, 60 } } }) },
			{ "backlight-colored", "Minimal backlight 0.05, colored.", with(_owner, { { "backlightThreshold", 0.05 }, { "backlightColored", true } }) },
			{ "backlight-white", "Minimal backlight 0.05, not colored.", with(_owner, { { "backlightThreshold", 0.05 }, { "backlightColored", false } }) },
			{ "backlight-too-high", "Minimal backlight 1.0 (reset to 0.0039 by the C++ code).", with(_owner, { { "backlightThreshold", 1.0 } }) },
			{ "backlight-off", "Minimal backlight 0.", with(_owner, { { "backlightThreshold", 0.0 } }) },
			{ "power-limit-0.5", "Power limit 0.5: the inputs are processed as one strip and scaled together.", with(_owner, { { "powerLimit", 0.5 } }) },
			{ "power-limit-0.5-dim", "Power limit 0.5 with a dim strip that stays below the limit.", with(_owner, { { "powerLimit", 0.5 } }), true },
			{ "combined", "Every step at once.", with(matrix, { { "temperatureSetting", "warm" }, { "scaleOutput", 0.9 }, { "gamma", 1.8 },
				{ "luminanceGain", 1.1 }, { "saturationGain", 1.2 }, { "backlightThreshold", 0.02 }, { "powerLimit", 0.8 } }) },
		};

		for (const Config& config : configs)
			GOLDEN_CHECK(QString("color/cpp-process-%1.json").arg(config.name), processFixture(config));
	}

	void colorOrder()
	{
		for (const QString& order : { "rgb", "bgr", "rbg", "grb", "gbr", "brg" })
			GOLDEN_CHECK(QString("color/cpp-order-%1.json").arg(order),
				processFixture({ order, QString("The owner's settings with the device color order %1 (applied by InfiniteProcessing).").arg(order), _owner }, order));
	}

	void calibrate()
	{
		const QJsonObject matrix = with(_owner, { { "red", QJsonArray{ 250, 10, 0 } }, { "green", QJsonArray{ 20, 240, 10 } }, { "blue", QJsonArray{ 0, 30, 220 } } });
		const QJsonObject rotation = with(_owner, { { "red", QJsonArray{ 0, 255, 0 } }, { "green", QJsonArray{ 0, 0, 255 } }, { "blue", QJsonArray{ 255, 0, 0 } } });

		const std::vector<Config> configs{
			{ "matrix", "Primaries only (classic_config): asymmetric targets. Shows which way the target matrix is applied.", matrix },
			{ "lut-primaries", "The same primaries through the 3D LUT (classic_config false, other corners ideal).", with(matrix, { { "classic_config", false } }) },
			{ "matrix-rotation", "Primaries only: red->green, green->blue, blue->red targets.", rotation },
			{ "lut-rotation", "The same rotation through the 3D LUT.", with(rotation, { { "classic_config", false }, { "cyan", QJsonArray{ 255, 0, 255 } },
				{ "magenta", QJsonArray{ 255, 255, 0 } }, { "yellow", QJsonArray{ 0, 255, 255 } } }) },
		};

		for (const Config& config : configs)
			GOLDEN_CHECK(QString("color/cpp-calibrate-%1.json").arg(config.name), calibrateFixture(config));
	}

	void rgbwClassic()
	{
		const std::vector<QString> algorithms{ "white_off", "subtract_minimum", "sub_min_cool_adjust", "sub_min_warm_adjust",
			"wled_auto", "wled_auto_max", "wled_auto_accurate", "hyperserial_cold_white", "hyperserial_neutral_white", "hyperserial_custom" };

		std::vector<ColorRgb> inputs;
		const int steps[] = { 0, 1, 64, 128, 200, 254, 255 };
		for (int r : steps)
			for (int g : steps)
				for (int b : steps)
					inputs.emplace_back(r, g, b);

		JsonNode cases = JsonNode::array();
		for (const QString& name : algorithms)
		{
			const RGBW::WhiteAlgorithm algorithm = RGBW::stringToWhiteAlgorithm(name);
			RGBW::RgbwChannelCorrection correction{};
			RGBW::prepareRgbwCalibration(correction, algorithm, 200, 180, 170, 120);

			std::vector<uint8_t> output;
			for (const ColorRgb& input : inputs)
			{
				ColorRgbw rgbw{};
				RGBW::rgb2rgbw(input, &rgbw, algorithm, correction);
				output.insert(output.end(), { rgbw.red, rgbw.green, rgbw.blue, rgbw.white });
			}
			cases.add(JsonNode::object().set("algorithm", name).set("expectedRgbw", golden::hex(output.data(), output.size())));
		}

		std::vector<uint8_t> inputBytes;
		for (const ColorRgb& input : inputs)
			inputBytes.insert(inputBytes.end(), { input.red, input.green, input.blue });

		const JsonNode document = JsonNode::object()
			.set("source", "C++ reference: RGBW::rgb2rgbw (sources/led-drivers/ColorRgbw.cpp, tests/golden/ColorGolden.cpp)")
			.set("description", "Classic RGB to RGBW conversion per white algorithm; hyperserial_custom uses gain 200, red 180, green 170, blue 120.")
			.set("inputs", golden::hex(inputBytes.data(), inputBytes.size()))
			.set("cases", cases);
		GOLDEN_CHECK("rgbw/cpp-classic.json", document);
	}

	void rgbwInfiniteEngine()
	{
		struct Setup
		{
			QString name;
			int interval;
			float whiteMixerThreshold;
			float whiteLedIntensity;
			float3 whitePoint;
			LedString::ColorOrder order;
		};
		const std::vector<Setup> setups{
			{ "default", 20, 0.0f, 1.0f, float3{ 1.0f }, LedString::ColorOrder::ORDER_RGB },
			{ "threshold-0.3-intensity-1.8", 20, 0.3f, 1.8f, float3{ 1.0f }, LedString::ColorOrder::ORDER_RGB },
			{ "warm-white-point", 10, 0.1f, 1.2f, float3{ 1.0f, 0.85f, 0.6f }, LedString::ColorOrder::ORDER_GRB },
			{ "white-off", 20, 0.0f, 0.0f, float3{ 1.0f }, LedString::ColorOrder::ORDER_BGR },
		};

		// a fade with small steps (dithering), a jump, a hold (repeated frames) and a fade to black
		std::vector<std::vector<float3>> frames;
		for (int i = 0; i < 24; i++)
		{
			const float t = i / 23.0f;
			std::vector<float3> frame;
			frame.emplace_back(0.30f + 0.002f * i, 0.20f + 0.001f * i, 0.10f);
			frame.emplace_back(t, t, t);
			frame.emplace_back(1.0f - t, 0.5f, t * 0.25f);
			frame.emplace_back((i < 12) ? 0.8f : 0.1f, 0.6f, 0.9f);
			frame.emplace_back(0.5f, 0.5f, 0.5f);
			frame.emplace_back(0.001f * (23 - i), 0.0005f * (23 - i), 0.0f);
			frames.push_back(frame);
		}

		JsonNode framesJson = JsonNode::array();
		for (const auto& frame : frames)
			framesJson.add(golden::vecs(frame));

		JsonNode cases = JsonNode::array();
		for (const Setup& setup : setups)
		{
			InfiniteColorEngineRgbw engine;
			JsonNode outputs = JsonNode::array();
			for (const auto& frame : frames)
			{
				std::vector<uint8_t> output;
				engine.renderRgbwFrame(frame, setup.interval, setup.whiteMixerThreshold, setup.whiteLedIntensity, setup.whitePoint, output, 0, setup.order);
				outputs.add(golden::hex(output.data(), output.size()));
			}
			cases.add(JsonNode::object()
				.set("name", setup.name)
				.set("interval", setup.interval)
				.set("whiteMixerThreshold", setup.whiteMixerThreshold)
				.set("whiteLedIntensity", setup.whiteLedIntensity)
				.set("whitePoint", golden::vec(setup.whitePoint))
				.set("colorOrder", LedString::colorOrderToString(setup.order))
				.set("expectedRgbw", outputs));
		}

		const JsonNode document = JsonNode::object()
			.set("source", "C++ reference: InfiniteColorEngineRgbw::renderRgbwFrame (tests/golden/ColorGolden.cpp)")
			.set("description", "Stateful RGBW encoding with dithering: the frames are fed in order to one encoder per case; outputs are RGBW bytes per frame.")
			.set("frames", framesJson)
			.set("cases", cases);
		GOLDEN_CHECK("rgbw/cpp-infinite-engine.json", document);
	}
};

QTEST_GUILESS_MAIN(ColorGolden)
#include "ColorGolden.moc"
