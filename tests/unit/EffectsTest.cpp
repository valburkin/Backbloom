// Characterization tests for the built-in (non-music) effects: the kept set is registered and each one renders light.

#include <QTest>
#include <QVector>

#include <algorithm>
#include <memory>
#include <set>
#include <string>

#include <effects/AnimationBase.h>
#include <effects/EffectManufactory.h>
#include <hyperimage/HyperImage.h>

namespace
{
	const std::set<std::string> AMBIENT_EFFECTS = { "Rainbow swirl", "Rainbow swirl fast", "Full color mood blobs", "Warm mood blobs", "Breath", "Candle" };

	bool isLit(const ColorRgb& color)
	{
		return color.red > 0 || color.green > 0 || color.blue > 0;
	}

	// drives an effect like Effect::run does and reports whether any frame contained light
	bool rendersLight(AnimationBase& effect)
	{
		HyperImage painter;
		effect.Init(painter, 10);

		QVector<ColorRgb> leds(64, ColorRgb::BLACK);
		for (int frame = 0; frame < 200; frame++)
		{
			effect.Play(painter);
			if (effect.hasLedData(leds))
			{
				if (std::any_of(leds.begin(), leds.end(), isLit))
					return true;
			}
			else
			{
				Image<ColorRgb> image = painter.renderImage();
				const ColorRgb* pixels = reinterpret_cast<const ColorRgb*>(image.rawMem());
				if (std::any_of(pixels, pixels + image.width() * image.height(), isLit))
					return true;
			}
		}
		return false;
	}
}

class EffectsTest : public QObject
{
	Q_OBJECT

private slots:
	void keptEffectsAreRegistered()
	{
		std::set<std::string> names;
		for (const auto& definition : overglow::effects::GET_ALL_EFFECTS())
			names.insert(definition.name);

		QCOMPARE(names, AMBIENT_EFFECTS);
	}

	void effectRendersLight_data()
	{
		QTest::addColumn<QString>("name");
		for (const auto& name : AMBIENT_EFFECTS)
			QTest::newRow(name.c_str()) << QString::fromStdString(name);
	}

	void effectRendersLight()
	{
		QFETCH(QString, name);

		const auto& effects = overglow::effects::GET_ALL_EFFECTS();
		auto definition = std::find_if(effects.begin(), effects.end(), [&name](const auto& ed) { return ed.name == name.toStdString(); });
		QVERIFY(definition != effects.end());

		std::unique_ptr<AnimationBase> effect(definition->factory());
		QVERIFY(!effect->isSoundEffect());
		QVERIFY(effect->GetSleepTime() > 0);
		QVERIFY(rendersLight(*effect));
	}
};

QTEST_GUILESS_MAIN(EffectsTest)
#include "EffectsTest.moc"
