// Golden fixtures for the ten kept effects: the six ambient effects and the four music effects, driven the way
// Effect::run drives them. Music effects get synthetic audio blocks through the real SoundCapture analysis (FFT).
//
// Determinism: the effects use no clock. "Candle" and the start hue of "Full color mood blobs" use rand(); the test
// seeds it with srand(seed) on its own thread, so the fixtures hold the MSVC CRT sequence
// (state = state * 214013 + 2531011; rand() = (state >> 16) & 0x7fff).

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QTest>

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <memory>
#include <set>

#include <base/LedString.h>
#include <base/SoundCapture.h>
#include <effects/Animation4Music_PulseMulti.h>
#include <effects/Animation4Music_QuatroMulti.h>
#include <effects/Animation4Music_StereoMulti.h>
#include <effects/Animation4Music_WavesPulse.h>
#include <effects/AnimationBase.h>
#include <effects/AnimationBaseMusic.h>
#include <effects/EffectManufactory.h>
#include <hyperimage/HyperImage.h>

#include "GoldenFixture.h"

using golden::JsonNode;

namespace
{
	constexpr unsigned SEED = 42;
	constexpr int LATCH_TIME = 10;	// Effect::start
	constexpr double PI = 3.14159265358979323846;

	// zlib stream (RFC 1950) of the RGB24 pixels, base64
	JsonNode image(const Image<ColorRgb>& img)
	{
		const QByteArray raw(reinterpret_cast<const char*>(img.rawMem()), static_cast<qsizetype>(img.width()) * img.height() * 3);
		const QByteArray zlib = qCompress(raw, 9).mid(4);
		return JsonNode::object()
			.set("width", img.width())
			.set("height", img.height())
			.set("fnv1a", golden::fnv1a(img.rawMem(), raw.size()))
			.set("rgbZlib", QString::fromLatin1(zlib.toBase64()));
	}

	JsonNode leds(const QVector<ColorRgb>& buffer)
	{
		const bool uniform = std::all_of(buffer.begin(), buffer.end(), [&buffer](const ColorRgb& c) { return c == buffer.front(); });
		const uint8_t* data = reinterpret_cast<const uint8_t*>(buffer.constData());
		return (uniform) ? JsonNode::object().set("color", golden::hex(data, 3))
			: JsonNode::object().set("leds", golden::hex(data, static_cast<size_t>(buffer.size()) * 3));
	}

	const EffectDefinition& definition(const std::string& name)
	{
		const auto& effects = overglow::effects::GET_ALL_EFFECTS();
		return *std::find_if(effects.begin(), effects.end(), [&name](const EffectDefinition& ed) { return ed.name == name; });
	}

	JsonNode header(const EffectDefinition& ed, const QString& description)
	{
		return JsonNode::object()
			.set("source", "C++ reference: sources/effects, driven like Effect::run (tests/golden/EffectsGolden.cpp)")
			.set("description", description)
			.set("name", ed.name)
			.set("smoothingDirectMode", ed.smoothingDirectMode)
			.set("smoothingTime", ed.smoothingTime)
			.set("smoothingFrequency", ed.smoothingFrequency);
	}

	class GoldenSoundCapture : public SoundCapture
	{
	public:
		GoldenSoundCapture()
			: SoundCapture(QJsonDocument(QJsonObject{ { "enable", true }, { "device", "golden" }, { "enable_smoothing", true } }))
		{
		}

		void start() override { _isRunning = true; }
		void stop() override { _isRunning = false; }
	};

	// a silent block, then bass / mids / treble in changing amounts
	std::vector<int16_t> audioBlock(int index)
	{
		std::vector<int16_t> samples(SOUNDCAP_N_WAVE, 0);
		if (index == 0)
			return samples;
		const double bass = (index % 3 == 1) ? 9000 : 1500;
		const double mid = 1000.0 * index;
		const double treble = (index % 2 == 0) ? 3000 : 300;
		for (int i = 0; i < SOUNDCAP_N_WAVE; i++)
		{
			const double t = i / double(SOUNDCAP_N_WAVE);
			const double v = bass * std::sin(2 * PI * 3 * t) + mid * std::sin(2 * PI * 45 * t) + treble * std::sin(2 * PI * 300 * t);
			samples[i] = static_cast<int16_t>(std::clamp(std::lround(v), -32768L, 32767L));
		}
		return samples;
	}
}

class EffectsGolden : public QObject
{
	Q_OBJECT

	QVector<ColorRgb> _leds;
	QSize _grid;

	// Effect::run for the effects that paint or set LEDs; frames lists the frame indices to record
	JsonNode runAmbient(const std::string& name, const std::vector<int>& frames, const QString& description)
	{
		const EffectDefinition& ed = definition(name);
		srand(SEED);
		std::unique_ptr<AnimationBase> effect(ed.factory());
		HyperImage painter(_grid.width(), _grid.height());
		effect->Init(painter, LATCH_TIME);

		QVector<ColorRgb> buffer(_leds.size(), ColorRgb::BLACK);
		JsonNode recorded = JsonNode::array();
		const int last = *std::max_element(frames.begin(), frames.end());
		for (int frame = 0; frame <= last; frame++)
		{
			effect->Play(painter);
			const bool hasLeds = effect->hasLedData(buffer);
			if (std::find(frames.begin(), frames.end(), frame) == frames.end())
				continue;

			JsonNode entry = (hasLeds) ? leds(buffer) : JsonNode::object().set("image", image(painter.renderImage()));
			entry.set("frame", frame).set("sleepTime", std::min(effect->GetSleepTime(), 100));
			recorded.add(entry);
		}

		return header(ed, description)
			.set("seed", SEED)
			.set("ledCount", _leds.size())
			.set("gridSize", JsonNode::array().add(_grid.width()).add(_grid.height()))
			.set("latchTime", LATCH_TIME)
			.set("frames", recorded);
	}

	JsonNode runMusic(const std::string& name)
	{
		const EffectDefinition& ed = definition(name);
		GoldenSoundCapture capture;
		std::unique_ptr<AnimationBase> effect(ed.factory());
		auto* music = dynamic_cast<AnimationBaseMusic*>(effect.get());
		music->hasResult = [&capture](AnimationBaseMusic* e, uint32_t& lastIndex, bool* newAverage, bool* newSlow, bool* newFast, int* isMulti) {
			return capture.hasResult(e, lastIndex, newAverage, newSlow, newFast, isMulti);
		};
		const uint32_t handle = capture.open();
		HyperImage painter(_grid.width(), _grid.height());
		effect->Init(painter, LATCH_TIME);

		JsonNode blocks = JsonNode::array();
		for (int block = 0; block < 8; block++)
		{
			std::vector<int16_t> samples = audioBlock(block);
			const QString audio = golden::base64(reinterpret_cast<const uint8_t*>(samples.data()), samples.size() * sizeof(int16_t));
			capture.analyzeSpectrum(samples.data(), SOUNDCAP_LOG2_N_WAVE);

			// Effect::run polls about four times per audio block; the first call sees the new result, the next two the
			// interpolated ("middle") ones, then nothing until the next block
			JsonNode frames = JsonNode::array();
			for (int call = 0; call < 4; call++)
			{
				Image<ColorRgb> img(80, 45);
				img.clear();
				frames.add((effect->getImage(img)) ? image(img) : JsonNode());
			}
			blocks.add(JsonNode::object().set("audio", audio).set("frames", frames));
		}
		capture.close(handle);

		return header(ed, "Audio blocks: 1024 signed 16-bit samples (little endian, base64) passed to SoundCapture::analyzeSpectrum; "
				"frames: getImage(80x45) called four times after each block, null when it returned false.")
			.set("sleepTime", effect->GetSleepTime())
			.set("blocks", blocks);
	}

private slots:
	void initTestCase()
	{
		Logger::getInstance()->setLogLevel(Logger::WARNING);
		const QJsonArray layout = golden::readTestFixture("leds-168.json").toArray();
		_leds = QVector<ColorRgb>(layout.size(), ColorRgb::BLACK);
		_grid = LedString::getLedLayoutGridSize(layout);

		std::set<std::string> names;
		for (const auto& ed : overglow::effects::GET_ALL_EFFECTS())
			names.insert(ed.name);
		QCOMPARE(names.size(), size_t(10));
	}

	void ambient()
	{
		std::vector<int> firstFrames(20);
		for (int i = 0; i < 20; i++)
			firstFrames[i] = i;
		std::vector<int> blobFrames = firstFrames;
		for (int i = 50; i <= 600; i += 50)
			blobFrames.push_back(i);
		std::vector<int> breathFrames(320);
		for (int i = 0; i < 320; i++)
			breathFrames[i] = i;

		GOLDEN_CHECK("effects/cpp-rainbow-swirl.json", runAmbient("Rainbow swirl", { 0, 1, 2, 45, 90, 135, 180, 225, 270, 315, 359, 360, 361 },
			"Images painted by the effect (conical gradient, one degree per frame); recorded frames only."));
		GOLDEN_CHECK("effects/cpp-rainbow-swirl-fast.json", runAmbient("Rainbow swirl fast", { 0, 1, 2 },
			"The same painting as Rainbow swirl with a shorter sleep time."));
		GOLDEN_CHECK("effects/cpp-full-color-mood-blobs.json", runAmbient("Full color mood blobs", blobFrames,
			"LED colors; the start hue comes from rand() after srand(seed)."));
		GOLDEN_CHECK("effects/cpp-warm-mood-blobs.json", runAmbient("Warm mood blobs", blobFrames, "LED colors."));
		GOLDEN_CHECK("effects/cpp-breath.json", runAmbient("Breath", breathFrames, "One color for all LEDs per frame (a fade cycle)."));
		GOLDEN_CHECK("effects/cpp-candle.json", runAmbient("Candle", firstFrames, "LED colors from rand() after srand(seed)."));
	}

	void music()
	{
		GOLDEN_CHECK("effects/cpp-music-pulse.json", runMusic(AMUSIC_PULSEMULTI));
		GOLDEN_CHECK("effects/cpp-music-quatro.json", runMusic(AMUSIC_QUATROMULTI));
		GOLDEN_CHECK("effects/cpp-music-stereo.json", runMusic(AMUSIC_STEREOMULTI));
		GOLDEN_CHECK("effects/cpp-music-waves-pulse.json", runMusic(AMUSIC_WAVESPULSE));
	}
};

QTEST_GUILESS_MAIN(EffectsGolden)
#include "EffectsGolden.moc"
