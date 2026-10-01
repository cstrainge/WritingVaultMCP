(() => {
  const group = window.WritingVaultTimeline.prototype.groupRelationshipTransitions;
  const relationship = { kind: 'Relationship', ref: 'relationship:one', label: 'Friends' };
  const entry = (name, ref, boundary, date, owner = relationship) => ({
    kind: 'RelationshipMembershipPeriod', ref, boundary, lane: 'Relationships',
    isMembershipTransition: true,
    title: `${name} ${boundary === 'Start' ? 'entered' : 'left'} a relationship.`,
    occurred: { kind: date.kind || 'ExactDate', lower: date.lower, upper: date.upper,
      calendarId: 'Gregorian', lowerInclusive: true, upperInclusive: true },
    related: [{ kind: 'Character', ref: `character:${name}`, label: name }, owner]
  });
  const sameDay = { lower: '2021-09-08', upper: '2021-09-08' };
  const inputs = [
    entry('Sam', 'period:sam', 'Start', sameDay),
    entry('Aurora', 'period:aurora', 'Start', sameDay),
    entry('Frankie', 'period:frankie', 'Start', sameDay),
    entry('Sam', 'period:sam', 'End', sameDay),
    entry('Aurora', 'period:aurora', 'End', sameDay),
    entry('Frankie', 'period:other', 'Start', sameDay,
      { kind: 'Relationship', ref: 'relationship:other', label: 'Another relationship' }),
    entry('Sam', 'period:fuzzy-sam', 'Start',
      { kind: 'Month', lower: '2021-09-01', upper: '2021-10-01' }),
    entry('Aurora', 'period:fuzzy-aurora', 'Start',
      { kind: 'Month', lower: '2021-09-01', upper: '2021-10-01' }),
    entry('Sam', 'period:late-sam', 'Start',
      { kind: 'ExactInstant', lower: '2021-09-09T09:00:00', upper: '2021-09-09T09:00:00' }),
    entry('Aurora', 'period:late-aurora', 'Start',
      { kind: 'ExactInstant', lower: '2021-09-09T10:00:00', upper: '2021-09-09T10:00:00' })
  ];
  const output = group(inputs);
  const expected = [
    'Sam, Aurora, and Frankie entered a relationship.',
    'Sam and Aurora left a relationship.',
    'Frankie entered a relationship.',
    'Sam entered a relationship.',
    'Aurora entered a relationship.',
    'Sam entered a relationship.',
    'Aurora entered a relationship.'
  ];
  const actual = output.map(item => item.title);
  if (JSON.stringify(actual) !== JSON.stringify(expected))
    throw new Error(`Unexpected relationship transition grouping: ${JSON.stringify(actual)}`);
  if (output[0].ref !== relationship.ref || output[0].related.filter(link => link.kind === 'Character').length !== 3)
    throw new Error('Combined entry lost its relationship or a participant.');
  if (window.WritingVaultTimeline.prototype.rangeTracks(inputs).spans.length)
    throw new Error('Relationship transitions were incorrectly drawn as duration tracks.');
  const organization = { kind: 'Organization', ref: 'organization:one', label: 'The Society' };
  const orgEntry = (name, ref, boundary, custom = false) => ({
    ...entry(name, ref, boundary, sameDay, organization), kind: 'OrganizationMembership',
    lane: 'Memberships', hasCustomDescription: custom,
    title: custom ? `${name} returned with a secret.` :
      `${name} ${boundary === 'Start' ? 'joined' : 'left'} organization The Society.`
  });
  const org = group([
    orgEntry('Sam', 'membership:sam', 'Start'),
    orgEntry('Aurora', 'membership:aurora', 'Start'),
    orgEntry('Frankie', 'membership:frankie', 'Start', true)
  ]);
  if (org.length !== 2 || org[0].title !== 'Sam and Aurora joined organization The Society.' ||
      org[1].title !== 'Frankie returned with a secret.')
    throw new Error(`Unexpected organization grouping: ${JSON.stringify(org.map(item => item.title))}`);
  return { passed: true, titles: actual };
})()
