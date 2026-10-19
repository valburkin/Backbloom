#ifndef PCH_ENABLED
	#include <list>
	#include <utility>
#endif

#include <base/SettingsMigration.h>
#include <utils/settings.h>

namespace
{
	const QString RAINBOW_SWIRL = "Rainbow swirl";
	const QString FULL_COLOR_MOOD_BLOBS = "Full color mood blobs";
	const QString WARM_MOOD_BLOBS = "Warm mood blobs";
	const QString BREATH = "Breath";
	const QString CANDLE = "Candle";
	const QString MUSIC_PULSE = "Music: fullscreen pulse (MULTI COLOR)";

	// effects of older versions that were folded into one of the kept effects
	const std::list<std::pair<QString, QString>> REMOVED_EFFECTS = {
		{ "Atomic swirl", RAINBOW_SWIRL },
		{ "Double swirl", RAINBOW_SWIRL },
		{ "Rainbow waves", RAINBOW_SWIRL },
		{ "Plasma", RAINBOW_SWIRL },
		{ "Sea waves", RAINBOW_SWIRL },
		{ "Waves with Color", RAINBOW_SWIRL },
		{ "Sparks", RAINBOW_SWIRL },
		{ "Blue mood blobs", FULL_COLOR_MOOD_BLOBS },
		{ "Cold mood blobs", FULL_COLOR_MOOD_BLOBS },
		{ "Green mood blobs", FULL_COLOR_MOOD_BLOBS },
		{ "Red mood blobs", WARM_MOOD_BLOBS },
		{ "Cinema brighten lights", CANDLE },
		{ "Cinema dim lights", CANDLE },
		{ "Notify blue", BREATH },
		{ "Strobe red", BREATH },
		{ "Strobe white", BREATH },
		{ "Police Lights Single", BREATH },
		{ "Police Lights Solid", BREATH },
		{ "Knight rider", BREATH },
		{ "System Shutdown", BREATH },
		{ "Music: equalizer test (turn on video preview)", MUSIC_PULSE }
	};

	// the color/speed variants of the music effects were folded into the multi color one of each kind
	const std::list<std::pair<QString, QString>> MUSIC_EFFECT_KINDS = {
		{ "Music: fullscreen pulse (", MUSIC_PULSE },
		{ "Music: quatro for LED strip (", "Music: quatro for LED strip (MULTI COLOR)" },
		{ "Music: stereo for LED strip (", "Music: stereo for LED strip (MULTI COLOR)" },
		{ "Music: pulse waves for LED strip (", "Music: pulse waves for LED strip (MULTI COLOR)" }
	};

	struct ObsoleteFields
	{
		settings::type type;
		const char* feature;
		QStringList fields;
	};

	// options of removed features: CEC and the signal detection of the system grabber (meant for capture cards)
	const std::list<ObsoleteFields> OBSOLETE_FIELDS = {
		{ settings::type::SYSTEMCONTROL, "CEC", { "cecControl" } },
		{ settings::type::SYSTEMGRABBER, "signal detection", { "signalDetection", "redSignalThreshold", "greenSignalThreshold", "blueSignalThreshold",
			"noSignalCounterThreshold", "sDVOffsetMin", "sDVOffsetMax", "sDHOffsetMin", "sDHOffsetMax" } }
	};
}

QString SettingsMigration::replacementEffect(const QString& effectName)
{
	for (const auto& [removed, replacement] : REMOVED_EFFECTS)
		if (effectName == removed)
			return replacement;

	for (const auto& [prefix, replacement] : MUSIC_EFFECT_KINDS)
		if (effectName.startsWith(prefix))
			return replacement;

	return effectName;
}

QStringList SettingsMigration::removeUnsupportedFeatures(QJsonObject& dbConfig)
{
	QStringList changes;

	for (const auto& [type, feature, fields] : OBSOLETE_FIELDS)
	{
		const QString key = settings::typeToString(type);
		QJsonObject object = dbConfig[key].toObject();
		bool modified = false;
		for (const auto& field : fields)
			if (object.contains(field))
			{
				object.remove(field);
				modified = true;
			}
		if (modified)
		{
			changes.append(QString("Removed the %1 options from the '%2' settings: the feature is not part of this version").arg(feature).arg(key));
			dbConfig[key] = object;
		}
	}

	for (const auto type : { settings::type::FGEFFECT, settings::type::BGEFFECT })
	{
		const QString key = settings::typeToString(type);
		QJsonObject object = dbConfig[key].toObject();
		const QString effect = object["effect"].toString();
		const QString replacement = replacementEffect(effect);
		if (!effect.isEmpty() && replacement != effect)
		{
			changes.append(QString("The '%1' effect of the '%2' settings is not part of this version, replaced with '%3'").arg(effect).arg(key).arg(replacement));
			object["effect"] = replacement;
			dbConfig[key] = object;
		}
	}

	return changes;
}
