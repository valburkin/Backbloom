// Golden fixtures for the six smoothing interpolators. Each one is driven the way InfiniteSmoothing drives it
// (parameters from the smoothing settings, setTargetColors on every captured frame, updateCurrentColors and
// getCurrentColors on every timer tick) with an injected time line instead of InternalClock.

#include <QJsonArray>
#include <QJsonObject>
#include <QTest>

#include <algorithm>
#include <cmath>
#include <memory>

#include <infinite-color-engine/InfiniteExponentialInterpolator.h>
#include <infinite-color-engine/InfiniteHybridInterpolator.h>
#include <infinite-color-engine/InfiniteHybridRgbInterpolator.h>
#include <infinite-color-engine/InfiniteRgbInterpolator.h>
#include <infinite-color-engine/InfiniteStepperInterpolator.h>
#include <infinite-color-engine/InfiniteYuvInterpolator.h>

#include "GoldenFixture.h"

using golden::JsonNode;
using namespace linalg::aliases;

namespace
{
	// InfiniteSmoothing::clearQueuedColors
	std::unique_ptr<InfiniteInterpolator> createInterpolator(const QString& type)
	{
		if (type == "HybridInterpolator")
			return std::make_unique<InfiniteHybridInterpolator>();
		if (type == "HybridRgbInterpolator")
			return std::make_unique<InfiniteHybridRgbInterpolator>();
		if (type == "YuvInterpolator")
			return std::make_unique<InfiniteYuvInterpolator>();
		if (type == "RgbInterpolator")
			return std::make_unique<InfiniteRgbInterpolator>();
		if (type == "ExponentialInterpolator")
			return std::make_unique<InfiniteExponentialInterpolator>();
		return std::make_unique<InfiniteStepperInterpolator>();
	}

	// the captured frames: a static start, a big change, a small change, flicker between two close colors, a fade to black
	std::vector<float3> targetAt(int frame)
	{
		if (frame < 6)
			return { { 0.0f, 0.0f, 0.0f }, { 1.0f, 1.0f, 1.0f }, { 0.2f, 0.4f, 0.6f } };
		if (frame < 20)
			return { { 1.0f, 0.0f, 0.0f }, { 0.0f, 0.0f, 1.0f }, { 0.9f, 0.8f, 0.1f } };
		if (frame < 30)
			return { { 0.95f, 0.05f, 0.02f }, { 0.02f, 0.03f, 0.9f }, { 0.85f, 0.78f, 0.15f } };
		if (frame < 45)
		{
			const float3 c = (frame % 2 == 0) ? float3{ 0.5f, 0.5f, 0.5f } : float3{ 0.55f, 0.5f, 0.45f };
			return { c, c, c };
		}
		return { { 0.0f, 0.0f, 0.0f }, { 0.0f, 0.0f, 0.0f }, { 0.0f, 0.0f, 0.0f } };
	}

	JsonNode run(const QString& name, const QString& description, const QJsonObject& settings, float minimalBacklight)
	{
		// InfiniteSmoothing::handleSignalInstanceSettingsChanged
		const int64_t settlingTime = settings["time_ms"].toInt(200);
		const int64_t updateInterval = static_cast<int64_t>(std::round(std::max(1000.0 / std::max(settings["updateFrequency"].toDouble(25), 20.0), 5.0)));
		const float smoothingFactor = static_cast<float>(settings["smoothingFactor"].toDouble(0.1));
		const float stiffness = static_cast<float>(settings["stiffness"].toDouble(200));
		const float damping = static_cast<float>(settings["damping"].toDouble(26));
		const float yLimit = static_cast<float>(settings["y_limit"].toDouble());

		auto interpolator = createInterpolator(settings["type"].toString());
		interpolator->setTransitionDuration(static_cast<float>(settlingTime));
		interpolator->setSmoothingFactor(smoothingFactor);
		interpolator->setSpringiness(stiffness, damping);
		interpolator->setMaxLuminanceChangePerFrame(yLimit);

		constexpr int FRAMES = 60;
		constexpr long long END = 1500;
		constexpr long long FIRST_TICK = 5;

		JsonNode steps = JsonNode::array();
		int frame = 0;
		long long tick = FIRST_TICK;
		while (frame < FRAMES || tick <= END)
		{
			// frames arrive at about 60 Hz (17/16/17 ms); a frame and a tick at the same time: the frame comes first
			const long long frameTime = (frame < FRAMES) ? (frame * 1000LL) / 60 : END + 1;
			if (frame < FRAMES && frameTime <= tick)
			{
				std::vector<float3> target = targetAt(frame);
				steps.add(JsonNode::object().set("t", frameTime).set("target", golden::vecs(target)));
				interpolator->setTargetColors(std::move(target), frameTime, false);
				frame++;
			}
			else
			{
				interpolator->updateCurrentColors(tick, minimalBacklight);
				const SharedOutputColors colors = interpolator->getCurrentColors(minimalBacklight);
				steps.add(JsonNode::object().set("t", tick).set("complete", interpolator->isAnimationComplete()).set("colors", golden::vecs(*colors)));
				tick += updateInterval;
			}
		}

		return JsonNode::object()
			.set("source", "C++ reference: the interpolators of InfiniteSmoothing (tests/golden/SmoothingGolden.cpp)")
			.set("description", description)
			.set("name", name)
			.set("settings", golden::fromQt(settings))
			.set("settlingTime", static_cast<long long>(settlingTime))
			.set("updateInterval", static_cast<long long>(updateInterval))
			.set("minimalBacklight", minimalBacklight)
			.set("tolerance", 0)
			.set("steps", steps);
	}
}

class SmoothingGolden : public QObject
{
	Q_OBJECT

	QJsonObject _owner;

private slots:
	void initTestCase()
	{
		Logger::getInstance()->setLogLevel(Logger::WARNING);
		_owner = golden::readTestFixture("legacy-settings.json").toObject()["smoothing"].toObject();
		QVERIFY(!_owner.isEmpty());
	}

	void interpolators()
	{
		// the owner's minimal backlight (color.channelAdjustment[0].backlightThreshold)
		const float minimalBacklight = 0.0039f;
		auto with = [this](std::initializer_list<std::pair<QString, QJsonValue>> changes) {
			QJsonObject settings = _owner;
			for (const auto& change : changes)
				settings[change.first] = change.second;
			return settings;
		};

		struct Variant
		{
			QString name;
			QString description;
			QJsonObject settings;
		};
		const std::vector<Variant> variants{
			{ "stepper", "The owner's smoothing settings with the Stepper interpolator.", with({ { "type", "Stepper" } }) },
			{ "rgb", "The owner's smoothing settings with the RgbInterpolator.", with({ { "type", "RgbInterpolator" } }) },
			{ "yuv", "The owner's smoothing settings with the YuvInterpolator.", with({ { "type", "YuvInterpolator" } }) },
			{ "hybrid", "The owner's smoothing settings with the HybridInterpolator.", with({ { "type", "HybridInterpolator" } }) },
			{ "exponential", "The owner's smoothing settings with the ExponentialInterpolator.", with({ { "type", "ExponentialInterpolator" } }) },
			{ "hybrid-rgb", "The owner's smoothing settings (HybridRgbInterpolator).", _owner },
			{ "rgb-factor-0.3", "RgbInterpolator with smoothingFactor 0.3.", with({ { "type", "RgbInterpolator" }, { "smoothingFactor", 0.3 } }) },
			{ "yuv-limit-0.02", "YuvInterpolator with smoothingFactor 0.2 and y_limit 0.02.", with({ { "type", "YuvInterpolator" }, { "smoothingFactor", 0.2 }, { "y_limit", 0.02 } }) },
			{ "hybrid-limit-0.05", "HybridInterpolator with y_limit 0.05, stiffness 200, damping 20.", with({ { "type", "HybridInterpolator" }, { "y_limit", 0.05 }, { "stiffness", 200 }, { "damping", 20 } }) },
			{ "hybrid-rgb-factor-0.3", "HybridRgbInterpolator with smoothingFactor 0.3, time 300 ms, 25 Hz.", with({ { "smoothingFactor", 0.3 }, { "time_ms", 300 }, { "updateFrequency", 25 } }) },
		};

		for (const Variant& variant : variants)
			GOLDEN_CHECK(QString("smoothing/cpp-%1.json").arg(variant.name), run(variant.name, variant.description, variant.settings, minimalBacklight));
	}
};

QTEST_GUILESS_MAIN(SmoothingGolden)
#include "SmoothingGolden.moc"
