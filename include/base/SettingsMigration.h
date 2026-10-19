#pragma once

#ifndef PCH_ENABLED
	#include <QJsonObject>
	#include <QString>
	#include <QStringList>
#endif

// Content based upgrades of settings written by older versions (or by the product this one is based on) that refer
// to features which are not part of this version. Pure functions on the settings object (type name -> config) of one
// instance, so they are idempotent and also cover restored backups.
namespace SettingsMigration
{
	// The effect that replaces an effect of an older version, or the name itself if it is still available
	QString replacementEffect(const QString& effectName);

	// Removes options of removed features and maps removed effects in the boot/background effect settings.
	// Returns a description of every change for the log (empty: nothing changed).
	QStringList removeUnsupportedFeatures(QJsonObject& dbConfig);
}
