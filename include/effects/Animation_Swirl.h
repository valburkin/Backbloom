#pragma once

#include <effects/AnimationBase.h>

// Rotating rainbow (conical gradient), presets: "Rainbow swirl" and "Rainbow swirl fast"
class Animation_Swirl : public AnimationBase
{
public:

	struct SwirlGradient
	{
		uint8_t items[5];
	};

	// rotationTime: duration of one full rotation [s]
	explicit Animation_Swirl(double rotationTime);

	void Init(
		HyperImage& hyperImage,
		int hyperLatchTime) override;

	bool Play(HyperImage& painter) override;

private:

	static int getSTime(int hyperLatchTime, int _rt, double steps = 360);

	static EffectDefinition definition(const char* name, double rotationTime, int smoothingTime = 0, int smoothingFrequency = 0);
	static bool isRegistered;

	const double _rotationTime;
	int _angle;
	std::vector<Animation_Swirl::SwirlGradient> _gradient;
};
