INSERT INTO [dbo].[stage_new_Category] (
[new_activedate],
[new_categorycode],
[new_categoryid],
[new_expirationdate],
[new_formid],
[new_legacyid])
SELECT m.[activedate],
m.[categorycode],
glt.[new_categoryid],
m.[expirationdate],
m.[formid],
m.[legacyid]
FROM [Legacy].[dbo].[Category] m 
LEFT JOIN [dbo].[guid_new_Category] as glt on glt.[new_legacyid] = m.[legacyid] 
OUTER APPLY
(SELECT TOP(1) DeltaDate FROM [dbo].[UpdateTime] WHERE [rowid] = 1) AS ud
WHERE m.[UpdateDate] >= ud.[DeltaDate] OR m.[CreateDate] >= ud.[DeltaDate]