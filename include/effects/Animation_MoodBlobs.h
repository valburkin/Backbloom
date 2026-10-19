#pragma once

#include <effects/AnimationBase.h>

// Slowly rotating color blobs whose base hue drifts within a range, presets: "Full color mood blobs" and "Warm mood blobs"
class Animation_MoodBlobs : public AnimationBase
{
public:
	struct Params
	{
		Point3d color;				// base color (unless colorRandom)
		bool    colorRandom;		// start from a random base color
		double  baseColorRangeLeft;	// range of the base hue drift [degrees]
		double  baseColorRangeRight;
		double  baseColorChangeRate;// seconds per hue step of the drift
	};

	explicit Animation_MoodBlobs(const Params& params);

	void Init(
		HyperImage& hyperImage,
		int hyperLatchTime) override;

	bool Play(HyperImage& painter) override;
	bool hasLedData(QVector<ColorRgb>& buffer) override;

private:
	int    hyperledCount;
	bool   fullColorWheelAvailable;
	double baseColorChangeIncreaseValue;
	double sleepTime;
	double amplitudePhaseIncrement;
	int    colorDataIncrement;
	double amplitudePhase;
	bool   rotateColors;
	int    baseColorChangeStepCount;
	double baseHSVValue;
	int    numberOfRotates;
	Point3dhsv baseHsv;

	std::vector<Point3d> colorData;

	static EffectDefinition definition(const char* name, const Params& params, int smoothingTime = 0, int smoothingFrequency = 0);
	static bool isRegistered;

	double rotationTime;
	Point3d color;
	bool   colorRandom;
	double hueChange;
	int    blobs;
	bool   reverse;
	bool   baseColorChange;
	double baseColorRangeLeft;
	double baseColorRangeRight;
	double baseColorChangeRate;
};
